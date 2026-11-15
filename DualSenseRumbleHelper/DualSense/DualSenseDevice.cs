using System.Drawing;
using System.ComponentModel;
using System.Runtime.InteropServices;
using DualSenseRumbleHelper.Native;
using Microsoft.Win32.SafeHandles;

namespace DualSenseRumbleHelper.DualSense;

/// <summary>Identity and HID capabilities of a connected DualSense.</summary>
public sealed record DualSenseInfo(string DevicePath, string InstanceId, ushort ProductId, bool IsBluetooth,
    int InputLength, int OutputLength, int FeatureLength)
{
    public string Transport => IsBluetooth ? "Bluetooth" : "USB";
    public string ModelName => ProductId == DualSenseLocator.DualSenseEdgePid ? "DualSense Edge" : "DualSense";
}

/// <summary>Decoded DualSense input report.</summary>
public struct DualSenseState
{
    public byte LX, LY, RX, RY, L2, R2;
    public int Dpad;                // 0 = up, 1 = up-right, ... 7 = up-left, 8 = released
    public bool Square, Cross, Circle, Triangle;
    public bool L1, R1, L2Button, R2Button, Create, Options, L3, R3;
    public bool PS, Touchpad, Mute;
    public int BatteryPercent;
    public bool Charging;
}

public static class DualSenseLocator
{
    public const ushort SonyVid = 0x054C;
    public const ushort DualSensePid = 0x0CE6;
    public const ushort DualSenseEdgePid = 0x0DF2;

    /// <summary>Finds all connected DualSense gamepad HID collections (USB and Bluetooth).</summary>
    public static unsafe List<DualSenseInfo> Enumerate()
    {
        var found = new List<DualSenseInfo>();
        Win32.HidD_GetHidGuid(out Guid hidGuid);
        foreach (var (path, instanceId) in Win32.EnumerateInterfaces(hidGuid))
        {
            // Query-only handle (works even if another app has the device open).
            using SafeFileHandle h = Win32.CreateFileW(path, 0, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
                IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;

            var attr = new Win32.HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<Win32.HIDD_ATTRIBUTES>() };
            if (!Win32.HidD_GetAttributes(h, ref attr)) continue;
            if (attr.VendorID != SonyVid || (attr.ProductID != DualSensePid && attr.ProductID != DualSenseEdgePid)) continue;

            if (!Win32.HidD_GetPreparsedData(h, out IntPtr pp)) continue;
            Win32.HIDP_CAPS caps;
            try { Win32.HidP_GetCaps(pp, out caps); }
            finally { Win32.HidD_FreePreparsedData(pp); }

            // Generic Desktop / Game Pad collection with the full report.
            if (caps.UsagePage != 0x01 || caps.Usage != 0x05 || caps.InputReportByteLength < 64) continue;

            bool bt = caps.InputReportByteLength != 64;
            found.Add(new DualSenseInfo(path, instanceId, attr.ProductID, bt, caps.InputReportByteLength,
                caps.OutputReportByteLength, caps.FeatureReportByteLength));
        }
        // Prefer USB when the same pad is connected both ways.
        return found.OrderBy(d => d.IsBluetooth ? 1 : 0).ToList();
    }

    /// <summary>All HID instance ids that belong to a DualSense (every collection), used for HidHide.</summary>
    public static unsafe List<string> EnumerateAllHidInstanceIds()
    {
        var ids = new List<string>();
        Win32.HidD_GetHidGuid(out Guid hidGuid);
        foreach (var (path, instanceId) in Win32.EnumerateInterfaces(hidGuid))
        {
            using SafeFileHandle h = Win32.CreateFileW(path, 0, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
                IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            var attr = new Win32.HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<Win32.HIDD_ATTRIBUTES>() };
            if (!Win32.HidD_GetAttributes(h, ref attr)) continue;
            if (attr.VendorID == SonyVid && (attr.ProductID == DualSensePid || attr.ProductID == DualSenseEdgePid) && instanceId.Length > 0)
                ids.Add(instanceId);
        }
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>
/// Opens a DualSense over raw HID, decodes input reports on a background thread and writes rumble output reports.
/// Supports USB (report 0x01 / 0x02) and Bluetooth (report 0x31 with CRC-32).
/// </summary>
public sealed class DualSenseDevice : IDisposable
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    private readonly SafeFileHandle _readHandle;   // overlapped
    private readonly SafeFileHandle _writeHandle;  // synchronous
    private readonly ManualResetEvent _stop = new(false);
    private readonly AutoResetEvent _rumbleSignal = new(false);
    private readonly object _rumbleLock = new();
    private Thread? _readThread, _writeThread;
    private byte _pendingLeft, _pendingRight;
    private bool _rumbleDirty;
    private byte _btSequence;
    private int _reportCounter;
    private long _rateWindowStart;
    private volatile bool _disconnectRaised;

    public DualSenseInfo Info { get; }
    public DualSenseState LastState { get; private set; }
    public int ReportsPerSecond { get; private set; }
    public int WriteErrors { get; private set; }
    public bool IsRunning => _readThread is { IsAlive: true };

    /// <summary>Raised on the reader thread for every decoded report.</summary>
    public event Action<DualSenseState>? StateReceived;
    /// <summary>Raised once when the device goes away (unplugged / BT off).</summary>
    public event Action<string>? Disconnected;

    public DualSenseDevice(DualSenseInfo info)
    {
        Info = info;
        _readHandle = Win32.CreateFileW(info.DevicePath, Win32.GENERIC_READ | Win32.GENERIC_WRITE,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (_readHandle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), T("err.padOpenRead"));
        _writeHandle = Win32.CreateFileW(info.DevicePath, Win32.GENERIC_READ | Win32.GENERIC_WRITE,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);
        if (_writeHandle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            _readHandle.Dispose();
            throw new Win32Exception(err, T("err.padOpenWrite"));
        }

        if (info.IsBluetooth) EnableFullBluetoothReports();
    }

    /// <summary>Reading feature report 0x05 (calibration) switches a BT DualSense from the 10-byte 0x01 report to the full 0x31 report.</summary>
    private unsafe void EnableFullBluetoothReports()
    {
        int len = Math.Max(Info.FeatureLength, 41);
        byte[] buf = new byte[len];
        buf[0] = 0x05;
        fixed (byte* p = buf) Win32.HidD_GetFeature(_writeHandle, p, (uint)len);
    }

    public void Start()
    {
        if (_readThread != null) return;
        _rateWindowStart = Environment.TickCount64;
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "DualSense-Read", Priority = ThreadPriority.AboveNormal };
        _writeThread = new Thread(WriteLoop) { IsBackground = true, Name = "DualSense-Write" };
        _readThread.Start();
        _writeThread.Start();
    }

    /// <summary>Queues a rumble update. left = big (low-frequency) motor, right = small (high-frequency) motor.</summary>
    /// <summary>Re-sends the current rumble values (used to push a new lightbar colour).</summary>
    public void PushOutput()
    {
        lock (_rumbleLock) _rumbleDirty = true;
        _rumbleSignal.Set();
    }

    public void SetRumble(byte left, byte right)
    {
        lock (_rumbleLock)
        {
            _pendingLeft = left;
            _pendingRight = right;
            _rumbleDirty = true;
        }
        _rumbleSignal.Set();
    }

    /// <summary>Stops the threads, turns the motors off and closes the handles.</summary>
    public void Stop()
    {
        if (_stop.WaitOne(0)) return;
        _stop.Set();
        _rumbleSignal.Set();
        try { unsafe { Win32.CancelIoEx(_readHandle, null); } } catch { }
        _readThread?.Join(1500);
        _writeThread?.Join(1500);
        try { WriteRumbleReport(0, 0); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _readHandle.Dispose();
        _writeHandle.Dispose();
        _stop.Dispose();
        _rumbleSignal.Dispose();
    }

    private unsafe void ReadLoop()
    {
        int len = Math.Max(Info.InputLength, 64);
        byte[] buffer = new byte[len];
        using var evt = new ManualResetEvent(false);
        IntPtr evtHandle = evt.SafeWaitHandle.DangerousGetHandle();

        fixed (byte* p = buffer)
        {
            while (!_stop.WaitOne(0))
            {
                var ov = new NativeOverlapped { EventHandle = evtHandle };
                evt.Reset();
                bool ok = Win32.ReadFile(_readHandle, p, (uint)len, out uint got, &ov);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != Win32.ERROR_IO_PENDING) { RaiseDisconnected($"ReadFile error {err}"); return; }
                    // Wait for data; wake up every second to notice Stop().
                    while (true)
                    {
                        uint w = Win32.WaitForSingleObject(evtHandle, 1000);
                        if (w == Win32.WAIT_OBJECT_0) break;
                        if (_stop.WaitOne(0)) { Win32.CancelIoEx(_readHandle, &ov); Win32.GetOverlappedResult(_readHandle, &ov, out _, true); return; }
                    }
                    if (!Win32.GetOverlappedResult(_readHandle, &ov, out got, false))
                    {
                        int e2 = Marshal.GetLastWin32Error();
                        if (e2 == Win32.ERROR_OPERATION_ABORTED && _stop.WaitOne(0)) return;
                        RaiseDisconnected($"Read aborted (error {e2})");
                        return;
                    }
                }
                if (TryParse(buffer, (int)got, out DualSenseState state))
                {
                    LastState = state;
                    CountReport();
                    StateReceived?.Invoke(state);
                }
            }
        }
    }

    private void CountReport()
    {
        _reportCounter++;
        long now = Environment.TickCount64;
        if (now - _rateWindowStart >= 1000)
        {
            ReportsPerSecond = (int)(_reportCounter * 1000L / Math.Max(1, now - _rateWindowStart));
            _reportCounter = 0;
            _rateWindowStart = now;
        }
    }

    private void WriteLoop()
    {
        int consecutiveErrors = 0;
        while (!_stop.WaitOne(0))
        {
            _rumbleSignal.WaitOne(500);
            if (_stop.WaitOne(0)) break;
            byte l, r;
            lock (_rumbleLock)
            {
                if (!_rumbleDirty) continue;
                l = _pendingLeft; r = _pendingRight; _rumbleDirty = false;
            }
            if (WriteRumbleReport(l, r)) consecutiveErrors = 0;
            else
            {
                WriteErrors++;
                if (++consecutiveErrors >= 8) { RaiseDisconnected(T("err.outputFailed")); return; }
            }
            Thread.Sleep(4); // never flood the controller (Bluetooth in particular)
        }
    }

    private void RaiseDisconnected(string reason)
    {
        if (_disconnectRaised) return;
        _disconnectRaised = true;
        Disconnected?.Invoke(reason);
    }

    /// <summary>Decodes USB report 0x01 or BT report 0x31.</summary>
    public bool TryParse(byte[] d, int len, out DualSenseState s)
    {
        s = default;
        int b;
        if (!Info.IsBluetooth && d[0] == 0x01 && len >= 64) b = 1;
        else if (Info.IsBluetooth && d[0] == 0x31 && len >= 78) b = 2;
        else return false;

        s.LX = d[b]; s.LY = d[b + 1]; s.RX = d[b + 2]; s.RY = d[b + 3];
        s.L2 = d[b + 4]; s.R2 = d[b + 5];
        byte b0 = d[b + 7], b1 = d[b + 8], b2 = d[b + 9];
        s.Dpad = b0 & 0x0F;
        s.Square = (b0 & 0x10) != 0; s.Cross = (b0 & 0x20) != 0; s.Circle = (b0 & 0x40) != 0; s.Triangle = (b0 & 0x80) != 0;
        s.L1 = (b1 & 0x01) != 0; s.R1 = (b1 & 0x02) != 0; s.L2Button = (b1 & 0x04) != 0; s.R2Button = (b1 & 0x08) != 0;
        s.Create = (b1 & 0x10) != 0; s.Options = (b1 & 0x20) != 0; s.L3 = (b1 & 0x40) != 0; s.R3 = (b1 & 0x80) != 0;
        s.PS = (b2 & 0x01) != 0; s.Touchpad = (b2 & 0x02) != 0; s.Mute = (b2 & 0x04) != 0;
        byte st = d[b + 52];
        int level = st & 0x0F, charge = (st >> 4) & 0x0F;
        s.BatteryPercent = charge == 2 ? 100 : Math.Min(100, level * 10 + 5);
        s.Charging = charge == 1;
        return true;
    }

    /// <summary>Optional lightbar colour. null = leave the controller's own lightbar alone.</summary>
    public Color? Lightbar { get; set; }
    private bool _ledsReleased;

    /// <summary>Builds and sends the output report. Returns false on failure.</summary>
    public unsafe bool WriteRumbleReport(byte left, byte right)
    {
        byte flag1 = 0;
        Color? lb = Lightbar;
        if (lb.HasValue)
        {
            flag1 |= 0x04;                       // lightbar control
            if (!_ledsReleased) { flag1 |= 0x08; _ledsReleased = true; } // take LED control from the firmware (needed on BT)
        }
        byte[] buf;
        if (!Info.IsBluetooth)
        {
            buf = new byte[48];
            buf[0] = 0x02;
            buf[1] = 0x03;   // valid_flag0: compatible vibration + haptics select
            buf[2] = flag1;  // valid_flag1
            buf[3] = right;  // small motor
            buf[4] = left;   // big motor
            buf[39] = 0x04;  // valid_flag2: compatible vibration v2 (firmware >= 2.24)
            if (lb.HasValue) { buf[45] = lb.Value.R; buf[46] = lb.Value.G; buf[47] = lb.Value.B; }
        }
        else
        {
            buf = new byte[78];
            buf[0] = 0x31;
            buf[1] = (byte)(_btSequence << 4);
            _btSequence = (byte)((_btSequence + 1) & 0x0F);
            buf[2] = 0x10;
            buf[3] = 0x03;
            buf[4] = flag1;
            buf[5] = right;
            buf[6] = left;
            buf[41] = 0x04;
            if (lb.HasValue) { buf[47] = lb.Value.R; buf[48] = lb.Value.G; buf[49] = lb.Value.B; }
            uint crc = 0xFFFFFFFF;
            crc = CrcUpdate(crc, 0xA2);
            for (int i = 0; i < 74; i++) crc = CrcUpdate(crc, buf[i]);
            crc ^= 0xFFFFFFFF;
            buf[74] = (byte)crc; buf[75] = (byte)(crc >> 8); buf[76] = (byte)(crc >> 16); buf[77] = (byte)(crc >> 24);
        }

        fixed (byte* p = buf)
        {
            if (Win32.WriteFile(_writeHandle, p, (uint)buf.Length, out _, null)) { LastWriteMethod = $"WriteFile {buf.Length}B"; return true; }
            int err = Marshal.GetLastWin32Error();
            if (err == Win32.ERROR_INVALID_USER_BUFFER && Info.OutputLength > buf.Length)
            {
                // Some stacks insist on OutputReportByteLength-sized buffers.
                byte[] padded = new byte[Info.OutputLength];
                Buffer.BlockCopy(buf, 0, padded, 0, buf.Length);
                fixed (byte* pp = padded)
                {
                    if (Win32.WriteFile(_writeHandle, pp, (uint)padded.Length, out _, null)) { LastWriteMethod = $"WriteFile padded {padded.Length}B"; return true; }
                }
            }
            // Last resort: control-pipe SET_REPORT.
            bool ok = Win32.HidD_SetOutputReport(_writeHandle, p, (uint)buf.Length);
            LastWriteMethod = ok ? $"HidD_SetOutputReport {buf.Length}B" : $"failed (error {err}/{Marshal.GetLastWin32Error()})";
            return ok;
        }
    }

    /// <summary>Which write path the last output report took (diagnostics).</summary>
    public string LastWriteMethod { get; private set; } = "-";

    private static uint CrcUpdate(uint crc, byte b) => CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }
}
