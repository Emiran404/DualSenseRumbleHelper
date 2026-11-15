/*
 * DualSense Rumble Helper - XInput proxy DLL (x64)
 *
 * Drop-in replacement for xinput1_3.dll (also usable as xinput1_4.dll /
 * xinput9_1_0.dll by renaming). Reads a Sony DualSense (USB or Bluetooth)
 * directly via HID and exposes it as XInput user 0. XInputSetState rumble is
 * forwarded to the DualSense output report (left = big motor, right = small).
 * When no DualSense is connected (or for other user indices) all calls are
 * forwarded to the real XInput DLL.
 *
 * Build: zig cc -shared -O2 -target x86_64-windows-gnu -o xinput1_3.dll xinput_proxy.c xinput_proxy.def
 * No dependency on hid.lib/setupapi.lib: everything is resolved at run time.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <string.h>
#include <wchar.h>

/* Exports and ordinals are defined in xinput_proxy.def */
#define XI_EXPORT

/* ------------------------------------------------------------------ */
/* XInput types (kept local so no SDK header is required)              */
/* ------------------------------------------------------------------ */
typedef struct { WORD wButtons; BYTE bLeftTrigger; BYTE bRightTrigger; SHORT sThumbLX; SHORT sThumbLY; SHORT sThumbRX; SHORT sThumbRY; } XINPUT_GAMEPAD;
typedef struct { DWORD dwPacketNumber; XINPUT_GAMEPAD Gamepad; } XINPUT_STATE;
typedef struct { WORD wLeftMotorSpeed; WORD wRightMotorSpeed; } XINPUT_VIBRATION;
typedef struct { BYTE Type; BYTE SubType; WORD Flags; XINPUT_GAMEPAD Gamepad; XINPUT_VIBRATION Vibration; } XINPUT_CAPABILITIES;
typedef struct { BYTE BatteryType; BYTE BatteryLevel; } XINPUT_BATTERY_INFORMATION;
typedef struct { WORD VirtualKey; WCHAR Unicode; WORD Flags; BYTE UserIndex; BYTE HidCode; } XINPUT_KEYSTROKE;

#define XI_DPAD_UP 0x0001
#define XI_DPAD_DOWN 0x0002
#define XI_DPAD_LEFT 0x0004
#define XI_DPAD_RIGHT 0x0008
#define XI_START 0x0010
#define XI_BACK 0x0020
#define XI_LTHUMB 0x0040
#define XI_RTHUMB 0x0080
#define XI_LSHOULDER 0x0100
#define XI_RSHOULDER 0x0200
#define XI_GUIDE 0x0400
#define XI_A 0x1000
#define XI_B 0x2000
#define XI_X 0x4000
#define XI_Y 0x8000

#define ERR_DEVICE_NOT_CONNECTED 1167
#define ERR_EMPTY 4306

/* ------------------------------------------------------------------ */
/* HID / SetupAPI, resolved dynamically                                */
/* ------------------------------------------------------------------ */
typedef struct { ULONG Size; USHORT VendorID; USHORT ProductID; USHORT VersionNumber; } HIDD_ATTRIBUTES_;
typedef struct {
    USHORT Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
    USHORT Reserved[17];
    USHORT NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
           NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
           NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
} HIDP_CAPS_;
typedef struct { DWORD cbSize; GUID InterfaceClassGuid; DWORD Flags; ULONG_PTR Reserved; } SP_DEVICE_INTERFACE_DATA_;
typedef struct { DWORD cbSize; WCHAR DevicePath[1]; } SP_DEVICE_INTERFACE_DETAIL_DATA_W_;

typedef void (WINAPI *PFN_HidD_GetHidGuid)(GUID*);
typedef BOOLEAN (WINAPI *PFN_HidD_GetAttributes)(HANDLE, HIDD_ATTRIBUTES_*);
typedef BOOLEAN (WINAPI *PFN_HidD_GetPreparsedData)(HANDLE, void**);
typedef BOOLEAN (WINAPI *PFN_HidD_FreePreparsedData)(void*);
typedef LONG (WINAPI *PFN_HidP_GetCaps)(void*, HIDP_CAPS_*);
typedef BOOLEAN (WINAPI *PFN_HidD_GetFeature)(HANDLE, void*, ULONG);
typedef BOOLEAN (WINAPI *PFN_HidD_SetOutputReport)(HANDLE, void*, ULONG);
typedef HANDLE (WINAPI *PFN_SetupDiGetClassDevsW)(const GUID*, PCWSTR, HWND, DWORD);
typedef BOOL (WINAPI *PFN_SetupDiEnumDeviceInterfaces)(HANDLE, void*, const GUID*, DWORD, SP_DEVICE_INTERFACE_DATA_*);
typedef BOOL (WINAPI *PFN_SetupDiGetDeviceInterfaceDetailW)(HANDLE, SP_DEVICE_INTERFACE_DATA_*, SP_DEVICE_INTERFACE_DETAIL_DATA_W_*, DWORD, DWORD*, void*);
typedef BOOL (WINAPI *PFN_SetupDiDestroyDeviceInfoList)(HANDLE);

static struct {
    PFN_HidD_GetHidGuid GetHidGuid;
    PFN_HidD_GetAttributes GetAttributes;
    PFN_HidD_GetPreparsedData GetPreparsedData;
    PFN_HidD_FreePreparsedData FreePreparsedData;
    PFN_HidP_GetCaps GetCaps;
    PFN_HidD_GetFeature GetFeature;
    PFN_HidD_SetOutputReport SetOutputReport;
    PFN_SetupDiGetClassDevsW GetClassDevs;
    PFN_SetupDiEnumDeviceInterfaces EnumInterfaces;
    PFN_SetupDiGetDeviceInterfaceDetailW GetDetail;
    PFN_SetupDiDestroyDeviceInfoList DestroyList;
    BOOL ok;
} g_api;

static BOOL load_api(void)
{
    HMODULE hid = LoadLibraryW(L"hid.dll");
    HMODULE setup = LoadLibraryW(L"setupapi.dll");
    if (!hid || !setup) return FALSE;
    g_api.GetHidGuid = (PFN_HidD_GetHidGuid)GetProcAddress(hid, "HidD_GetHidGuid");
    g_api.GetAttributes = (PFN_HidD_GetAttributes)GetProcAddress(hid, "HidD_GetAttributes");
    g_api.GetPreparsedData = (PFN_HidD_GetPreparsedData)GetProcAddress(hid, "HidD_GetPreparsedData");
    g_api.FreePreparsedData = (PFN_HidD_FreePreparsedData)GetProcAddress(hid, "HidD_FreePreparsedData");
    g_api.GetCaps = (PFN_HidP_GetCaps)GetProcAddress(hid, "HidP_GetCaps");
    g_api.GetFeature = (PFN_HidD_GetFeature)GetProcAddress(hid, "HidD_GetFeature");
    g_api.SetOutputReport = (PFN_HidD_SetOutputReport)GetProcAddress(hid, "HidD_SetOutputReport");
    g_api.GetClassDevs = (PFN_SetupDiGetClassDevsW)GetProcAddress(setup, "SetupDiGetClassDevsW");
    g_api.EnumInterfaces = (PFN_SetupDiEnumDeviceInterfaces)GetProcAddress(setup, "SetupDiEnumDeviceInterfaces");
    g_api.GetDetail = (PFN_SetupDiGetDeviceInterfaceDetailW)GetProcAddress(setup, "SetupDiGetDeviceInterfaceDetailW");
    g_api.DestroyList = (PFN_SetupDiDestroyDeviceInfoList)GetProcAddress(setup, "SetupDiDestroyDeviceInfoList");
    g_api.ok = g_api.GetHidGuid && g_api.GetAttributes && g_api.GetPreparsedData && g_api.FreePreparsedData &&
               g_api.GetCaps && g_api.GetFeature && g_api.SetOutputReport && g_api.GetClassDevs &&
               g_api.EnumInterfaces && g_api.GetDetail && g_api.DestroyList;
    return g_api.ok;
}

/* ------------------------------------------------------------------ */
/* Real XInput forwarding                                              */
/* ------------------------------------------------------------------ */
typedef DWORD (WINAPI *PFN_GetState)(DWORD, XINPUT_STATE*);
typedef DWORD (WINAPI *PFN_SetState)(DWORD, XINPUT_VIBRATION*);
typedef DWORD (WINAPI *PFN_GetCaps)(DWORD, DWORD, XINPUT_CAPABILITIES*);
typedef void  (WINAPI *PFN_Enable)(BOOL);
typedef DWORD (WINAPI *PFN_GetDSound)(DWORD, GUID*, GUID*);
typedef DWORD (WINAPI *PFN_GetBattery)(DWORD, BYTE, XINPUT_BATTERY_INFORMATION*);
typedef DWORD (WINAPI *PFN_GetKeystroke)(DWORD, DWORD, XINPUT_KEYSTROKE*);
typedef DWORD (WINAPI *PFN_GetAudioIds)(DWORD, LPWSTR, UINT*, LPWSTR, UINT*);
typedef DWORD (WINAPI *PFN_WaitGuide)(DWORD, DWORD, void*);
typedef DWORD (WINAPI *PFN_CancelGuide)(DWORD);
typedef DWORD (WINAPI *PFN_PowerOff)(DWORD);

static struct {
    HMODULE mod;
    PFN_GetState GetState;
    PFN_SetState SetState;
    PFN_GetCaps GetCaps;
    PFN_Enable Enable;
    PFN_GetDSound GetDSound;
    PFN_GetBattery GetBattery;
    PFN_GetKeystroke GetKeystroke;
    PFN_GetAudioIds GetAudioIds;
    PFN_GetState GetStateEx;     /* ordinal 100 */
    PFN_WaitGuide WaitGuide;     /* ordinal 101 */
    PFN_CancelGuide CancelGuide; /* ordinal 102 */
    PFN_PowerOff PowerOff;       /* ordinal 103 */
} g_real;

static HMODULE g_self;

static HMODULE try_load_real(const WCHAR* path)
{
    HMODULE m = LoadLibraryExW(path, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (m == g_self) { /* never forward to ourselves */
        FreeLibrary(m);
        return NULL;
    }
    return m;
}

static void load_real(void)
{
    WCHAR dir[MAX_PATH], path[MAX_PATH + 64], sys[MAX_PATH];
    DWORD n = GetModuleFileNameW(g_self, dir, MAX_PATH);
    if (n > 0 && n < MAX_PATH) {
        WCHAR* slash = wcsrchr(dir, L'\\');
        if (slash) *slash = 0;
        /* 1) the original DLL that the installer renamed next to us */
        static const WCHAR* local_names[] = { L"xinput1_3_original.dll", L"xinput1_4_original.dll", L"xinput9_1_0_original.dll" };
        for (int i = 0; i < 3 && !g_real.mod; i++) {
            lstrcpynW(path, dir, MAX_PATH); lstrcatW(path, L"\\"); lstrcatW(path, local_names[i]);
            if (GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES) g_real.mod = try_load_real(path);
        }
    }
    /* 2) system copies */
    if (!g_real.mod && GetSystemDirectoryW(sys, MAX_PATH)) {
        static const WCHAR* sys_names[] = { L"xinput1_3.dll", L"xinput1_4.dll", L"xinput9_1_0.dll" };
        for (int i = 0; i < 3 && !g_real.mod; i++) {
            lstrcpynW(path, sys, MAX_PATH); lstrcatW(path, L"\\"); lstrcatW(path, sys_names[i]);
            if (GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES) g_real.mod = try_load_real(path);
        }
    }
    if (!g_real.mod) return;
    g_real.GetState = (PFN_GetState)GetProcAddress(g_real.mod, "XInputGetState");
    g_real.SetState = (PFN_SetState)GetProcAddress(g_real.mod, "XInputSetState");
    g_real.GetCaps = (PFN_GetCaps)GetProcAddress(g_real.mod, "XInputGetCapabilities");
    g_real.Enable = (PFN_Enable)GetProcAddress(g_real.mod, "XInputEnable");
    g_real.GetDSound = (PFN_GetDSound)GetProcAddress(g_real.mod, "XInputGetDSoundAudioDeviceGuids");
    g_real.GetBattery = (PFN_GetBattery)GetProcAddress(g_real.mod, "XInputGetBatteryInformation");
    g_real.GetKeystroke = (PFN_GetKeystroke)GetProcAddress(g_real.mod, "XInputGetKeystroke");
    g_real.GetAudioIds = (PFN_GetAudioIds)GetProcAddress(g_real.mod, "XInputGetAudioDeviceIds");
    g_real.GetStateEx = (PFN_GetState)GetProcAddress(g_real.mod, (LPCSTR)(ULONG_PTR)100);
    g_real.WaitGuide = (PFN_WaitGuide)GetProcAddress(g_real.mod, (LPCSTR)(ULONG_PTR)101);
    g_real.CancelGuide = (PFN_CancelGuide)GetProcAddress(g_real.mod, (LPCSTR)(ULONG_PTR)102);
    g_real.PowerOff = (PFN_PowerOff)GetProcAddress(g_real.mod, (LPCSTR)(ULONG_PTR)103);
}

/* ------------------------------------------------------------------ */
/* DualSense state                                                     */
/* ------------------------------------------------------------------ */
#define SONY_VID 0x054C
#define DS_PID 0x0CE6
#define DS_EDGE_PID 0x0DF2

static CRITICAL_SECTION g_lock;
static volatile LONG g_init_state;      /* 0 = not started, 1 = initialising, 2 = ready */
static volatile LONG g_stop;
static volatile LONG g_connected;
static volatile LONG g_bluetooth;
static volatile LONG g_enabled = TRUE;  /* XInputEnable */
static HANDLE g_thread;
static HANDLE g_rumble_event;
static volatile LONG g_rumble_pending;  /* (left << 8) | right, -1 = none */
static volatile HANDLE g_write_handle;  /* non-overlapped handle used for output reports */
static volatile LONG g_write_is_bt;
static volatile LONG g_write_len;

static XINPUT_GAMEPAD g_pad;
static DWORD g_packet;
static BYTE g_battery_percent;
static BYTE g_charging;

static DWORD g_crc_table[256];

static void crc_init(void)
{
    for (DWORD i = 0; i < 256; i++) {
        DWORD c = i;
        for (int k = 0; k < 8; k++) c = (c & 1) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
        g_crc_table[i] = c;
    }
}

static DWORD crc_update(DWORD crc, const BYTE* p, size_t n)
{
    while (n--) crc = g_crc_table[(crc ^ *p++) & 0xFF] ^ (crc >> 8);
    return crc;
}

static SHORT axis_x(BYTE v) { return (SHORT)((int)v * 257 - 32768); }
static SHORT axis_y(BYTE v) { return (SHORT)(32767 - (int)v * 257); }   /* DualSense Y: 0 = up, XInput: +32767 = up */

/* Parse a DualSense input report into the shared XINPUT_GAMEPAD. */
static void parse_report(const BYTE* d, DWORD len, BOOL bt)
{
    int b;
    if (!bt && d[0] == 0x01 && len >= 64) b = 1;
    else if (bt && d[0] == 0x31 && len >= 78) b = 2;
    else return;

    XINPUT_GAMEPAD g;
    g.sThumbLX = axis_x(d[b + 0]);
    g.sThumbLY = axis_y(d[b + 1]);
    g.sThumbRX = axis_x(d[b + 2]);
    g.sThumbRY = axis_y(d[b + 3]);
    g.bLeftTrigger = d[b + 4];
    g.bRightTrigger = d[b + 5];
    BYTE b0 = d[b + 7], b1 = d[b + 8], b2 = d[b + 9];
    WORD w = 0;
    switch (b0 & 0x0F) {
        case 0: w |= XI_DPAD_UP; break;
        case 1: w |= XI_DPAD_UP | XI_DPAD_RIGHT; break;
        case 2: w |= XI_DPAD_RIGHT; break;
        case 3: w |= XI_DPAD_RIGHT | XI_DPAD_DOWN; break;
        case 4: w |= XI_DPAD_DOWN; break;
        case 5: w |= XI_DPAD_DOWN | XI_DPAD_LEFT; break;
        case 6: w |= XI_DPAD_LEFT; break;
        case 7: w |= XI_DPAD_LEFT | XI_DPAD_UP; break;
        default: break;
    }
    if (b0 & 0x10) w |= XI_X;        /* square */
    if (b0 & 0x20) w |= XI_A;        /* cross */
    if (b0 & 0x40) w |= XI_B;        /* circle */
    if (b0 & 0x80) w |= XI_Y;        /* triangle */
    if (b1 & 0x01) w |= XI_LSHOULDER;
    if (b1 & 0x02) w |= XI_RSHOULDER;
    if (b1 & 0x10) w |= XI_BACK;     /* create */
    if (b1 & 0x20) w |= XI_START;    /* options */
    if (b1 & 0x40) w |= XI_LTHUMB;
    if (b1 & 0x80) w |= XI_RTHUMB;
    if (b2 & 0x01) w |= XI_GUIDE;    /* PS */
    g.wButtons = w;

    BYTE st = d[b + 52];
    BYTE lvl = st & 0x0F, chg = (st >> 4) & 0x0F;
    BYTE pct = (BYTE)((chg == 2) ? 100 : ((lvl * 10 + 5) > 100 ? 100 : lvl * 10 + 5));

    EnterCriticalSection(&g_lock);
    if (memcmp(&g, &g_pad, sizeof(g)) != 0) { g_pad = g; g_packet++; }
    g_battery_percent = pct;
    g_charging = (chg == 1);
    LeaveCriticalSection(&g_lock);
}

/* Send a rumble output report. left = big/low-frequency motor, right = small/high-frequency motor. */
static BOOL send_rumble(HANDLE h, BOOL bt, DWORD outLen, BYTE left, BYTE right)
{
    BYTE buf[600];
    DWORD n;
    memset(buf, 0, sizeof(buf));
    if (!bt) {
        buf[0] = 0x02;
        buf[1] = 0x03;          /* valid_flag0: compatible vibration + haptics select */
        buf[2] = 0x00;          /* valid_flag1: leave LEDs untouched */
        buf[3] = right;
        buf[4] = left;
        buf[39] = 0x04;         /* valid_flag2: compatible vibration v2 (newer firmware) */
        n = 48;
    } else {
        static BYTE seq = 0;
        buf[0] = 0x31;
        buf[1] = (BYTE)(seq << 4); seq = (BYTE)((seq + 1) & 0x0F);
        buf[2] = 0x10;
        buf[3] = 0x03;
        buf[4] = 0x00;
        buf[5] = right;
        buf[6] = left;
        buf[41] = 0x04;
        BYTE seed = 0xA2;
        DWORD crc = 0xFFFFFFFFu;
        crc = crc_update(crc, &seed, 1);
        crc = crc_update(crc, buf, 74);
        crc ^= 0xFFFFFFFFu;
        buf[74] = (BYTE)crc; buf[75] = (BYTE)(crc >> 8); buf[76] = (BYTE)(crc >> 16); buf[77] = (BYTE)(crc >> 24);
        n = 78;
    }
    DWORD written = 0;
    if (WriteFile(h, buf, n, &written, NULL)) return TRUE;
    /* Some stacks insist on the full OutputReportByteLength. */
    if (outLen > n && outLen <= sizeof(buf) && WriteFile(h, buf, outLen, &written, NULL)) return TRUE;
    return g_api.SetOutputReport(h, buf, n) ? TRUE : FALSE;
}

/* Try to open the first connected DualSense. Returns overlapped read handle, fills write handle + caps. */
static HANDLE open_dualsense(HANDLE* writeHandle, BOOL* bt, DWORD* inLen, DWORD* outLen, DWORD* featLen)
{
    GUID hidGuid;
    g_api.GetHidGuid(&hidGuid);
    HANDLE set = g_api.GetClassDevs(&hidGuid, NULL, NULL, 0x12 /* DIGCF_PRESENT | DIGCF_DEVICEINTERFACE */);
    if (set == INVALID_HANDLE_VALUE) return NULL;
    HANDLE result = NULL;
    SP_DEVICE_INTERFACE_DATA_ ifd;
    for (DWORD idx = 0; !result; idx++) {
        memset(&ifd, 0, sizeof(ifd)); ifd.cbSize = sizeof(ifd);
        if (!g_api.EnumInterfaces(set, NULL, &hidGuid, idx, &ifd)) break;
        DWORD need = 0;
        g_api.GetDetail(set, &ifd, NULL, 0, &need, NULL);
        if (need == 0 || need > 4096) continue;
        BYTE raw[4096];
        SP_DEVICE_INTERFACE_DETAIL_DATA_W_* det = (SP_DEVICE_INTERFACE_DETAIL_DATA_W_*)raw;
        det->cbSize = 8;
        if (!g_api.GetDetail(set, &ifd, det, need, &need, NULL)) continue;

        HANDLE hq = CreateFileW(det->DevicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
        if (hq == INVALID_HANDLE_VALUE) continue;
        HIDD_ATTRIBUTES_ attr; memset(&attr, 0, sizeof(attr)); attr.Size = sizeof(attr);
        BOOL match = g_api.GetAttributes(hq, &attr) && attr.VendorID == SONY_VID && (attr.ProductID == DS_PID || attr.ProductID == DS_EDGE_PID);
        HIDP_CAPS_ caps; memset(&caps, 0, sizeof(caps));
        if (match) {
            void* pp = NULL;
            if (g_api.GetPreparsedData(hq, &pp)) { g_api.GetCaps(pp, &caps); g_api.FreePreparsedData(pp); }
            match = (caps.UsagePage == 0x01 && caps.Usage == 0x05 && caps.InputReportByteLength >= 64);
        }
        CloseHandle(hq);
        if (!match) continue;

        HANDLE hr = CreateFileW(det->DevicePath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, NULL);
        if (hr == INVALID_HANDLE_VALUE) continue;
        HANDLE hw = CreateFileW(det->DevicePath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
        if (hw == INVALID_HANDLE_VALUE) { CloseHandle(hr); continue; }
        *writeHandle = hw;
        *bt = (caps.InputReportByteLength != 64);
        *inLen = caps.InputReportByteLength;
        *outLen = caps.OutputReportByteLength;
        *featLen = caps.FeatureReportByteLength;
        result = hr;
    }
    g_api.DestroyList(set);
    return result;
}

static void reset_state(void)
{
    EnterCriticalSection(&g_lock);
    memset(&g_pad, 0, sizeof(g_pad));
    g_packet++;
    LeaveCriticalSection(&g_lock);
}

static DWORD WINAPI worker(LPVOID arg)
{
    (void)arg;
    BYTE buf[1024];
    HANDLE readEvent = CreateEventW(NULL, TRUE, FALSE, NULL);
    while (!g_stop) {
        HANDLE hw = NULL; BOOL bt = FALSE; DWORD inLen = 0, outLen = 0, featLen = 0;
        HANDLE hr = open_dualsense(&hw, &bt, &inLen, &outLen, &featLen);
        if (!hr) {
            /* poll for hot-plug once per second */
            for (int i = 0; i < 10 && !g_stop; i++) Sleep(100);
            continue;
        }
        if (bt && featLen >= 41) {
            /* Requesting the calibration feature report switches BT controllers to full 0x31 reports. */
            BYTE feat[600]; memset(feat, 0, sizeof(feat)); feat[0] = 0x05;
            g_api.GetFeature(hw, feat, featLen > sizeof(feat) ? sizeof(feat) : featLen);
        }
        g_write_is_bt = bt; g_write_len = outLen; g_write_handle = hw;
        g_bluetooth = bt;
        InterlockedExchange(&g_connected, 1);
        InterlockedExchange(&g_rumble_pending, -1);
        BYTE curL = 0, curR = 0;
        DWORD lastWrite = 0;
        BOOL dirty = FALSE;
        DWORD readLen = inLen ? inLen : 64;
        if (readLen > sizeof(buf)) readLen = sizeof(buf);

        for (;;) {
            if (g_stop) break;
            OVERLAPPED ov; memset(&ov, 0, sizeof(ov)); ov.hEvent = readEvent; ResetEvent(readEvent);
            DWORD got = 0;
            BOOL ok = ReadFile(hr, buf, readLen, &got, &ov);
            if (!ok && GetLastError() != ERROR_IO_PENDING) break;
            HANDLE waits[2] = { readEvent, g_rumble_event };
            BOOL readDone = FALSE, lost = FALSE;
            while (!readDone && !lost) {
                DWORD w = WaitForMultipleObjects(2, waits, FALSE, 1000);
                if (w == WAIT_OBJECT_0) {
                    if (!GetOverlappedResult(hr, &ov, &got, FALSE)) lost = TRUE; else readDone = TRUE;
                } else if (w == WAIT_OBJECT_0 + 1 || w == WAIT_TIMEOUT) {
                    if (g_stop) { CancelIoEx(hr, &ov); GetOverlappedResult(hr, &ov, &got, TRUE); lost = TRUE; break; }
                    LONG pend = InterlockedExchange(&g_rumble_pending, -1);
                    if (pend >= 0) { curL = (BYTE)(pend >> 8); curR = (BYTE)(pend & 0xFF); dirty = TRUE; }
                    if (dirty) {
                        DWORD now = GetTickCount();
                        if (now - lastWrite >= 4) {
                            if (!send_rumble(hw, bt, outLen, curL, curR)) { /* ignore transient write errors */ }
                            lastWrite = now; dirty = FALSE;
                        } else {
                            Sleep(1);
                        }
                    }
                    if (w == WAIT_TIMEOUT) {
                        /* no input for a second: check if the device is still there */
                        DWORD dummy;
                        if (!GetOverlappedResult(hr, &ov, &dummy, FALSE) && GetLastError() != ERROR_IO_INCOMPLETE) lost = TRUE;
                    }
                } else { lost = TRUE; }
            }
            if (lost) break;
            if (!g_enabled) continue;
            parse_report(buf, got, bt);
        }
        /* disconnected or stopping */
        InterlockedExchange(&g_connected, 0);
        g_write_handle = NULL;
        send_rumble(hw, bt, outLen, 0, 0);
        CancelIoEx(hr, NULL);
        CloseHandle(hr);
        CloseHandle(hw);
        reset_state();
        if (!g_stop) Sleep(500);
    }
    CloseHandle(readEvent);
    return 0;
}

static void ensure_init(void)
{
    LONG prev = InterlockedCompareExchange(&g_init_state, 1, 0);
    if (prev == 2) return;
    if (prev == 1) { while (g_init_state != 2) Sleep(0); return; }
    crc_init();
    InitializeCriticalSection(&g_lock);
    g_rumble_event = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_rumble_pending = -1;
    load_real();
    if (load_api()) {
        g_thread = CreateThread(NULL, 0, worker, NULL, 0, NULL);
    }
    InterlockedExchange(&g_init_state, 2);
}

/* Index mapping: when the DualSense is present it takes user 0 and real pads shift up by one. */
static BOOL ds_active(DWORD user) { return user == 0 && g_connected; }
static DWORD real_index(DWORD user) { return g_connected ? user - 1 : user; }

/* ------------------------------------------------------------------ */
/* Exports                                                             */
/* ------------------------------------------------------------------ */
static DWORD get_state_impl(DWORD user, XINPUT_STATE* st, BOOL includeGuide)
{
    ensure_init();
    if (!st) return ERROR_BAD_ARGUMENTS;
    if (ds_active(user)) {
        EnterCriticalSection(&g_lock);
        st->dwPacketNumber = g_packet;
        st->Gamepad = g_pad;
        LeaveCriticalSection(&g_lock);
        if (!g_enabled) memset(&st->Gamepad, 0, sizeof(st->Gamepad));
        if (!includeGuide) st->Gamepad.wButtons &= (WORD)~XI_GUIDE;
        return ERROR_SUCCESS;
    }
    DWORD idx = real_index(user);
    if (idx > 3) return ERR_DEVICE_NOT_CONNECTED;
    if (includeGuide && g_real.GetStateEx) return g_real.GetStateEx(idx, st);
    if (g_real.GetState) return g_real.GetState(idx, st);
    return ERR_DEVICE_NOT_CONNECTED;
}

XI_EXPORT DWORD WINAPI XInputGetState(DWORD dwUserIndex, XINPUT_STATE* pState)
{
    return get_state_impl(dwUserIndex, pState, FALSE);
}

XI_EXPORT DWORD WINAPI XInputGetStateEx(DWORD dwUserIndex, XINPUT_STATE* pState)
{
    return get_state_impl(dwUserIndex, pState, TRUE);
}

XI_EXPORT DWORD WINAPI XInputSetState(DWORD dwUserIndex, XINPUT_VIBRATION* pVibration)
{
    ensure_init();
    if (!pVibration) return ERROR_BAD_ARGUMENTS;
    if (ds_active(dwUserIndex)) {
        BYTE l = (BYTE)(pVibration->wLeftMotorSpeed / 257);
        BYTE r = (BYTE)(pVibration->wRightMotorSpeed / 257);
        if (!g_enabled) { l = 0; r = 0; }
        InterlockedExchange(&g_rumble_pending, ((LONG)l << 8) | r);
        SetEvent(g_rumble_event);
        return ERROR_SUCCESS;
    }
    DWORD idx = real_index(dwUserIndex);
    if (idx > 3) return ERR_DEVICE_NOT_CONNECTED;
    if (g_real.SetState) return g_real.SetState(idx, pVibration);
    return ERR_DEVICE_NOT_CONNECTED;
}

XI_EXPORT DWORD WINAPI XInputGetCapabilities(DWORD dwUserIndex, DWORD dwFlags, XINPUT_CAPABILITIES* pCaps)
{
    ensure_init();
    if (!pCaps) return ERROR_BAD_ARGUMENTS;
    if (ds_active(dwUserIndex)) {
        memset(pCaps, 0, sizeof(*pCaps));
        pCaps->Type = 0x01;      /* XINPUT_DEVTYPE_GAMEPAD */
        pCaps->SubType = 0x01;   /* XINPUT_DEVSUBTYPE_GAMEPAD */
        pCaps->Flags = (WORD)(0x0001 /* FFB_SUPPORTED */ | (g_bluetooth ? 0x0002 /* WIRELESS */ : 0));
        pCaps->Gamepad.wButtons = 0xF3FF;
        pCaps->Gamepad.bLeftTrigger = 0xFF;
        pCaps->Gamepad.bRightTrigger = 0xFF;
        pCaps->Gamepad.sThumbLX = (SHORT)0xFFC0;
        pCaps->Gamepad.sThumbLY = (SHORT)0xFFC0;
        pCaps->Gamepad.sThumbRX = (SHORT)0xFFC0;
        pCaps->Gamepad.sThumbRY = (SHORT)0xFFC0;
        pCaps->Vibration.wLeftMotorSpeed = 0xFF;
        pCaps->Vibration.wRightMotorSpeed = 0xFF;
        return ERROR_SUCCESS;
    }
    DWORD idx = real_index(dwUserIndex);
    if (idx > 3) return ERR_DEVICE_NOT_CONNECTED;
    if (g_real.GetCaps) return g_real.GetCaps(idx, dwFlags, pCaps);
    return ERR_DEVICE_NOT_CONNECTED;
}

XI_EXPORT void WINAPI XInputEnable(BOOL enable)
{
    ensure_init();
    InterlockedExchange(&g_enabled, enable ? TRUE : FALSE);
    if (!enable && g_connected) {
        InterlockedExchange(&g_rumble_pending, 0);
        SetEvent(g_rumble_event);
    }
    if (g_real.Enable) g_real.Enable(enable);
}

XI_EXPORT DWORD WINAPI XInputGetDSoundAudioDeviceGuids(DWORD dwUserIndex, GUID* pRender, GUID* pCapture)
{
    ensure_init();
    if (ds_active(dwUserIndex)) {
        if (pRender) memset(pRender, 0, sizeof(GUID));
        if (pCapture) memset(pCapture, 0, sizeof(GUID));
        return ERROR_SUCCESS;
    }
    DWORD idx = real_index(dwUserIndex);
    if (idx > 3) return ERR_DEVICE_NOT_CONNECTED;
    if (g_real.GetDSound) return g_real.GetDSound(idx, pRender, pCapture);
    return ERR_DEVICE_NOT_CONNECTED;
}

XI_EXPORT DWORD WINAPI XInputGetBatteryInformation(DWORD dwUserIndex, BYTE devType, XINPUT_BATTERY_INFORMATION* pInfo)
{
    ensure_init();
    if (!pInfo) return ERROR_BAD_ARGUMENTS;
    if (ds_active(dwUserIndex)) {
        if (devType != 0 /* BATTERY_DEVTYPE_GAMEPAD */) { pInfo->BatteryType = 0; pInfo->BatteryLevel = 0; return ERROR_SUCCESS; }
        BYTE pct;
        EnterCriticalSection(&g_lock); pct = g_battery_percent; LeaveCriticalSection(&g_lock);
        pInfo->BatteryType = g_bluetooth ? 0x03 /* NIMH */ : 0x01 /* WIRED */;
        pInfo->BatteryLevel = (BYTE)(pct > 75 ? 3 : pct > 40 ? 2 : pct > 10 ? 1 : 0);
        return ERROR_SUCCESS;
    }
    DWORD idx = real_index(dwUserIndex);
    if (idx > 3) return ERR_DEVICE_NOT_CONNECTED;
    if (g_real.GetBattery) return g_real.GetBattery(idx, devType, pInfo);
    return ERR_DEVICE_NOT_CONNECTED;
}

XI_EXPORT DWORD WINAPI XInputGetKeystroke(DWORD dwUserIndex, DWORD dwReserved, XINPUT_KEYSTROKE* pKeystroke)
{
    ensure_init();
    if (dwUserIndex == 0xFF /* XUSER_INDEX_ANY */) {
        if (g_real.GetKeystroke) return g_real.GetKeystroke(dwUserIndex, dwReserved, pKeystroke);
        return ERR_EMPTY;
    }
    if (ds_active(dwUserIndex)) return ERR_EMPTY;
    DWORD idx = real_index(dwUserIndex);
    if (idx > 3) return ERR_DEVICE_NOT_CONNECTED;
    if (g_real.GetKeystroke) return g_real.GetKeystroke(idx, dwReserved, pKeystroke);
    return ERR_DEVICE_NOT_CONNECTED;
}

XI_EXPORT DWORD WINAPI XInputGetAudioDeviceIds(DWORD dwUserIndex, LPWSTR pRenderDeviceId, UINT* pRenderCount, LPWSTR pCaptureDeviceId, UINT* pCaptureCount)
{
    ensure_init();
    if (ds_active(dwUserIndex)) {
        if (pRenderCount) *pRenderCount = 0;
        if (pCaptureCount) *pCaptureCount = 0;
        return ERROR_SUCCESS;
    }
    DWORD idx = real_index(dwUserIndex);
    if (idx > 3) return ERR_DEVICE_NOT_CONNECTED;
    if (g_real.GetAudioIds) return g_real.GetAudioIds(idx, pRenderDeviceId, pRenderCount, pCaptureDeviceId, pCaptureCount);
    return ERR_DEVICE_NOT_CONNECTED;
}

XI_EXPORT DWORD WINAPI XInputWaitForGuideButton(DWORD dwUserIndex, DWORD dwFlags, void* pReserved)
{
    ensure_init();
    if (ds_active(dwUserIndex)) return ERR_DEVICE_NOT_CONNECTED;
    DWORD idx = real_index(dwUserIndex);
    if (idx > 3) return ERR_DEVICE_NOT_CONNECTED;
    if (g_real.WaitGuide) return g_real.WaitGuide(idx, dwFlags, pReserved);
    return ERR_DEVICE_NOT_CONNECTED;
}

XI_EXPORT DWORD WINAPI XInputCancelGuideButtonWait(DWORD dwUserIndex)
{
    ensure_init();
    if (ds_active(dwUserIndex)) return ERROR_SUCCESS;
    DWORD idx = real_index(dwUserIndex);
    if (idx > 3) return ERR_DEVICE_NOT_CONNECTED;
    if (g_real.CancelGuide) return g_real.CancelGuide(idx);
    return ERR_DEVICE_NOT_CONNECTED;
}

XI_EXPORT DWORD WINAPI XInputPowerOffController(DWORD dwUserIndex)
{
    ensure_init();
    if (ds_active(dwUserIndex)) return ERROR_SUCCESS;
    DWORD idx = real_index(dwUserIndex);
    if (idx > 3) return ERR_DEVICE_NOT_CONNECTED;
    if (g_real.PowerOff) return g_real.PowerOff(idx);
    return ERR_DEVICE_NOT_CONNECTED;
}

/* Diagnostic export used by the helper app's self-test (not part of XInput). */
XI_EXPORT DWORD WINAPI RumbleHelperProxyInfo(DWORD* pConnected, DWORD* pBluetooth, DWORD* pHasReal)
{
    ensure_init();
    if (pConnected) *pConnected = g_connected ? 1 : 0;
    if (pBluetooth) *pBluetooth = g_bluetooth ? 1 : 0;
    if (pHasReal) *pHasReal = g_real.mod ? 1 : 0;
    return 0x00010000; /* version 1.0 */
}

BOOL WINAPI DllMain(HINSTANCE hinst, DWORD reason, LPVOID reserved)
{
    if (reason == DLL_PROCESS_ATTACH) {
        g_self = (HMODULE)hinst;
        DisableThreadLibraryCalls(hinst);
    } else if (reason == DLL_PROCESS_DETACH) {
        InterlockedExchange(&g_stop, 1);
        /* Best effort: make sure the motors are off when the game goes away. */
        HANDLE hw = g_write_handle;
        if (hw && g_init_state == 2) {
            send_rumble(hw, g_write_is_bt, (DWORD)g_write_len, 0, 0);
        }
        if (reserved == NULL && g_thread) {
            /* FreeLibrary path: give the worker a moment to exit (never block for long inside DllMain). */
            SetEvent(g_rumble_event);
            WaitForSingleObject(g_thread, 200);
        }
    }
    return TRUE;
}
