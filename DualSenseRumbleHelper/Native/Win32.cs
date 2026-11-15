using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DualSenseRumbleHelper.Native;

/// <summary>P/Invoke surface used by the HID, ViGEm and device-enumeration code.</summary>
internal static unsafe class Win32
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

    public const int ERROR_IO_PENDING = 997;
    public const int ERROR_IO_INCOMPLETE = 996;
    public const int ERROR_OPERATION_ABORTED = 995;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_DEVICE_NOT_CONNECTED = 1167;
    public const int ERROR_INVALID_USER_BUFFER = 1784;

    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 258;

    public const uint DIGCF_PRESENT = 0x2;
    public const uint DIGCF_DEVICEINTERFACE = 0x10;

    public const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;
    public const uint CM_LOCATE_DEVNODE_NORMAL = 0;

    // ---- kernel32 ----
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle hFile, byte* lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, NativeOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(SafeFileHandle hFile, byte* lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, NativeOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, void* lpInBuffer,
        uint nInBufferSize, void* lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, NativeOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetOverlappedResult(SafeFileHandle hFile, NativeOverlapped* lpOverlapped,
        out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(SafeFileHandle hFile, NativeOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForMultipleObjects(uint nCount, IntPtr* lpHandles, bool bWaitAll, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeLibrary(IntPtr hModule);

    // ---- setupapi ----
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        byte* deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        StringBuilder deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    // ---- hid ----
    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll", ExactSpelling = true)]
    public static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", ExactSpelling = true)]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", ExactSpelling = true)]
    public static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("hid.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject, byte* reportBuffer, uint reportBufferLength);

    [DllImport("hid.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool HidD_SetOutputReport(SafeFileHandle hidDeviceObject, byte* reportBuffer, uint reportBufferLength);

    [DllImport("hid.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool HidD_GetProductString(SafeFileHandle hidDeviceObject, byte* buffer, uint bufferLength);

    // ---- cfgmgr32 ----
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int CM_Get_Device_ID_List_SizeW(out uint pulLen, string? pszFilter, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int CM_Get_Device_ID_ListW(string? pszFilter, char* buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    public static extern int CM_Get_Parent(out uint pdnDevInstParent, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int CM_Get_Device_IDW(uint dnDevInst, char* buffer, uint bufferLen, uint ulFlags);

    // ---- xinput (used only by the self-test) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_VIBRATION
    {
        public ushort wLeftMotorSpeed;
        public ushort wRightMotorSpeed;
    }

    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    public static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    public static extern uint XInputSetState(uint dwUserIndex, ref XINPUT_VIBRATION pVibration);

    // ---- helpers ----

    /// <summary>Enumerates device interface paths (and device instance ids) for a device interface class GUID.</summary>
    public static List<(string Path, string InstanceId)> EnumerateInterfaces(Guid interfaceGuid)
    {
        var result = new List<(string, string)>();
        IntPtr set = SetupDiGetClassDevsW(ref interfaceGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;
        try
        {
            for (uint index = 0; ; index++)
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref interfaceGuid, index, ref ifData)) break;

                var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                SetupDiGetDeviceInterfaceDetailW(set, ref ifData, null, 0, out uint required, ref devInfo);
                if (required == 0 || required > 8192) continue;

                byte[] detail = new byte[required];
                string path;
                fixed (byte* buffer = detail)
                {
                    *(uint*)buffer = 8; // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W on x64
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref ifData, buffer, required, out required, ref devInfo)) continue;
                    path = new string((char*)(buffer + 4));
                }

                var sb = new StringBuilder(512);
                string instanceId = SetupDiGetDeviceInstanceIdW(set, ref devInfo, sb, (uint)sb.Capacity, out _) ? sb.ToString() : "";
                result.Add((path, instanceId));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    /// <summary>Returns all present device instance ids (PnP) on the machine.</summary>
    public static List<string> GetPresentDeviceIds()
    {
        var list = new List<string>();
        if (CM_Get_Device_ID_List_SizeW(out uint len, null, CM_GETIDLIST_FILTER_PRESENT) != 0 || len == 0) return list;
        char[] buf = new char[len];
        fixed (char* p = buf)
        {
            if (CM_Get_Device_ID_ListW(null, p, len, CM_GETIDLIST_FILTER_PRESENT) != 0) return list;
        }
        int start = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            if (buf[i] == '\0')
            {
                if (i > start) list.Add(new string(buf, start, i - start));
                start = i + 1;
                if (i + 1 < buf.Length && buf[i + 1] == '\0') break;
            }
        }
        return list;
    }

    /// <summary>Returns the device instance id of the parent devnode, or null.</summary>
    public static string? GetParentDeviceId(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) != 0) return null;
        if (CM_Get_Parent(out uint parent, devInst, 0) != 0) return null;
        char* buf = stackalloc char[512];
        if (CM_Get_Device_IDW(parent, buf, 512, 0) != 0) return null;
        return new string(buf);
    }
}
