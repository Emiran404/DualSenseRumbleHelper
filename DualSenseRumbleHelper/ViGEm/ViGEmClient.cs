using System.ComponentModel;
using System.Runtime.InteropServices;
using DualSenseRumbleHelper.Native;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DualSenseRumbleHelper.ViGEm;

/// <summary>Xbox 360 input report as understood by ViGEmBus (XUSB_REPORT).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct XusbReport
{
    public ushort Buttons;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short ThumbLX;
    public short ThumbLY;
    public short ThumbRX;
    public short ThumbRY;

    public const ushort DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008;
    public const ushort Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080;
    public const ushort LeftShoulder = 0x0100, RightShoulder = 0x0200, Guide = 0x0400;
    public const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
}

/// <summary>
/// Minimal managed client for the ViGEmBus kernel driver (Xbox 360 targets only), speaking the
/// same IOCTL protocol as Nefarius' ViGEmClient library. No native helper DLL required.
/// </summary>
public sealed unsafe class ViGEmClient : IDisposable
{
    public static readonly Guid BusInterfaceGuid = new("96E42B22-F5E9-42F8-B043-ED0F932F014F");

    // CTL_CODE(FILE_DEVICE_BUS_EXTENDER = 0x2A, function, METHOD_BUFFERED, access)
    private const uint IOCTL_VIGEM_PLUGIN_TARGET = 0x2AA004;
    private const uint IOCTL_VIGEM_UNPLUG_TARGET = 0x2AA008;
    private const uint IOCTL_VIGEM_CHECK_VERSION = 0x2AA00C;
    private const uint IOCTL_VIGEM_WAIT_DEVICE_READY = 0x2AA010;
    private const uint IOCTL_XUSB_REQUEST_NOTIFICATION = 0x2AE804;
    private const uint IOCTL_XUSB_SUBMIT_REPORT = 0x2AA808;
    private const uint IOCTL_XUSB_GET_USER_INDEX = 0x2AE81C;
    private const uint VIGEM_COMMON_VERSION = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct PluginTarget { public uint Size; public uint SerialNo; public int TargetType; public ushort VendorId; public ushort ProductId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SerialOnly { public uint Size; public uint SerialNo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CheckVersion { public uint Size; public uint Version; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SubmitReport { public uint Size; public uint SerialNo; public XusbReport Report; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RequestNotification { public uint Size; public uint SerialNo; public byte LargeMotor; public byte SmallMotor; public byte LedNumber; }
    [StructLayout(LayoutKind.Sequential)]
    private struct GetUserIndex { public uint Size; public uint SerialNo; public uint UserIndex; }

    private SafeFileHandle? _bus;

    public bool IsConnected => _bus is { IsInvalid: false, IsClosed: false };

    /// <summary>True when the ViGEmBus driver service is registered on this machine.</summary>
    public static bool IsDriverInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ViGEmBus");
            if (key != null) return true;
        }
        catch { }
        return File.Exists(Path.Combine(Environment.SystemDirectory, "drivers", "ViGEmBus.sys"));
    }

    /// <summary>True when the bus device interface is present (driver loaded and running).</summary>
    public static bool IsBusPresent() => Win32.EnumerateInterfaces(BusInterfaceGuid).Count > 0;

    public void Connect()
    {
        if (IsConnected) return;
        var interfaces = Win32.EnumerateInterfaces(BusInterfaceGuid);
        if (interfaces.Count == 0)
            throw new InvalidOperationException(T("err.vigemNoDevice"));

        Exception? last = null;
        foreach (var (path, _) in interfaces)
        {
            var h = Win32.CreateFileW(path, Win32.GENERIC_READ | Win32.GENERIC_WRITE, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
                IntPtr.Zero, Win32.OPEN_EXISTING,
                Win32.FILE_ATTRIBUTE_NORMAL | Win32.FILE_FLAG_NO_BUFFERING | Win32.FILE_FLAG_WRITE_THROUGH | Win32.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) { last = new Win32Exception(Marshal.GetLastWin32Error(), T("err.vigemOpen")); continue; }
            try
            {
                var v = new CheckVersion { Size = (uint)sizeof(CheckVersion), Version = VIGEM_COMMON_VERSION };
                Ioctl(h, IOCTL_VIGEM_CHECK_VERSION, &v, v.Size, null, 0);
                _bus = h;
                return;
            }
            catch (Exception ex)
            {
                last = new InvalidOperationException(T("err.vigemVersion") + " " + ex.Message, ex);
                h.Dispose();
            }
        }
        throw last ?? new InvalidOperationException(T("err.vigemConnect"));
    }

    /// <summary>Plugs in a virtual Xbox 360 controller and returns its serial number (slot on the bus).</summary>
    public uint PlugInXbox360()
    {
        var bus = RequireBus();
        Exception? last = null;
        for (uint serial = 1; serial <= 16; serial++)
        {
            var plugin = new PluginTarget { Size = (uint)sizeof(PluginTarget), SerialNo = serial, TargetType = 0 /* Xbox360Wired */, VendorId = 0x045E, ProductId = 0x028E };
            try { Ioctl(bus, IOCTL_VIGEM_PLUGIN_TARGET, &plugin, plugin.Size, null, 0); }
            catch (Exception ex) { last = ex; continue; }

            var ready = new SerialOnly { Size = (uint)sizeof(SerialOnly), SerialNo = serial };
            try { Ioctl(bus, IOCTL_VIGEM_WAIT_DEVICE_READY, &ready, ready.Size, null, 0); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == Win32.ERROR_INVALID_PARAMETER)
            {
                // pre-1.17 driver: no wait IOCTL, device is considered ready
            }
            catch (Exception)
            {
                try { Unplug(serial); } catch { }
                throw;
            }
            return serial;
        }
        throw new InvalidOperationException(T("err.vigemNoSlot") + " " + last?.Message);
    }

    public void Unplug(uint serial)
    {
        var bus = RequireBus();
        var u = new SerialOnly { Size = (uint)sizeof(SerialOnly), SerialNo = serial };
        Ioctl(bus, IOCTL_VIGEM_UNPLUG_TARGET, &u, u.Size, null, 0);
    }

    public void Submit(uint serial, in XusbReport report)
    {
        var bus = RequireBus();
        var s = new SubmitReport { Size = (uint)sizeof(SubmitReport), SerialNo = serial, Report = report };
        Ioctl(bus, IOCTL_XUSB_SUBMIT_REPORT, &s, s.Size, null, 0);
    }

    /// <summary>XInput user index (0-3) the virtual pad received, or null if unknown.</summary>
    public uint? GetXInputUserIndex(uint serial)
    {
        var bus = RequireBus();
        var g = new GetUserIndex { Size = (uint)sizeof(GetUserIndex), SerialNo = serial };
        try
        {
            Ioctl(bus, IOCTL_XUSB_GET_USER_INDEX, &g, g.Size, &g, g.Size);
            return g.UserIndex;
        }
        catch { return null; }
    }

    /// <summary>
    /// Blocks until the game sends a rumble/LED update to the virtual pad, or until cancelled.
    /// Returns false when the request was cancelled or the target disappeared.
    /// </summary>
    public bool WaitForFeedback(uint serial, WaitHandle cancel, out byte largeMotor, out byte smallMotor, out byte ledNumber)
    {
        largeMotor = smallMotor = ledNumber = 0;
        var bus = RequireBus();
        var req = new RequestNotification { Size = (uint)sizeof(RequestNotification), SerialNo = serial };
        using var evt = new ManualResetEvent(false);
        var ov = new NativeOverlapped { EventHandle = evt.SafeWaitHandle.DangerousGetHandle() };
        Win32.DeviceIoControl(bus, IOCTL_XUSB_REQUEST_NOTIFICATION, &req, req.Size, &req, req.Size, out _, &ov);
        int err = Marshal.GetLastWin32Error();
        if (err != 0 && err != Win32.ERROR_IO_PENDING) return false;

        int signalled = WaitHandle.WaitAny(new[] { evt, cancel });
        if (signalled == 1)
        {
            Win32.CancelIoEx(bus, &ov);
            Win32.GetOverlappedResult(bus, &ov, out _, true);
            return false;
        }
        if (!Win32.GetOverlappedResult(bus, &ov, out _, false)) return false;
        largeMotor = req.LargeMotor;
        smallMotor = req.SmallMotor;
        ledNumber = req.LedNumber;
        return true;
    }

    public void Dispose()
    {
        _bus?.Dispose();
        _bus = null;
    }

    private SafeFileHandle RequireBus() => _bus is { IsInvalid: false, IsClosed: false } ? _bus : throw new InvalidOperationException(T("err.vigemNotConnected"));

    private static void Ioctl(SafeFileHandle h, uint code, void* input, uint inputSize, void* output, uint outputSize)
    {
        using var evt = new ManualResetEvent(false);
        var ov = new NativeOverlapped { EventHandle = evt.SafeWaitHandle.DangerousGetHandle() };
        bool ok = Win32.DeviceIoControl(h, code, input, inputSize, output, outputSize, out _, &ov);
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != Win32.ERROR_IO_PENDING) throw new Win32Exception(err);
        }
        if (!Win32.GetOverlappedResult(h, &ov, out _, true))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
