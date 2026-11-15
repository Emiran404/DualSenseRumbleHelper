using System.Runtime.InteropServices;
using System.Text;
using DualSenseRumbleHelper.DualSense;
using DualSenseRumbleHelper.Native;
using DualSenseRumbleHelper.ViGEm;

namespace DualSenseRumbleHelper.Services;

/// <summary>
/// `DualSenseRumbleHelper.exe --selftest [outfile]` : exercises HID enumeration, the embedded proxy DLL and
/// the ViGEm path end-to-end (virtual pad visible to XInput, rumble round trip) without any UI.
/// </summary>
public static class SelfTest
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetStateFn(uint user, out Win32.XINPUT_STATE state);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ProxyInfoFn(out uint connected, out uint bluetooth, out uint hasReal);

    public static int Run(string? outFile)
    {
        var sb = new StringBuilder();
        int failures = 0;
        void Line(string s) { sb.AppendLine(s); }
        void Check(bool ok, string what) { Line((ok ? "[PASS] " : "[FAIL] ") + what); if (!ok) failures++; }

        Line($"DualSense Rumble Helper self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        // 1) HID enumeration
        try
        {
            var pads = DualSenseLocator.Enumerate();
            Line($"[INFO] DualSense bulundu: {pads.Count}");
            foreach (var p in pads) Line($"       {p.ModelName} {p.Transport} in={p.InputLength} out={p.OutputLength} feat={p.FeatureLength} id={p.InstanceId}");
            Check(true, "HID enumeration");
            if (pads.Count > 0)
            {
                using var dev = new DualSenseDevice(pads[0]);
                int count = 0;
                dev.StateReceived += _ => Interlocked.Increment(ref count);
                dev.Start();
                Thread.Sleep(1000);
                Check(count > 0, $"DualSense input reports (1 s içinde {count} rapor)");
                bool w = dev.WriteRumbleReport(120, 120);
                Thread.Sleep(300);
                dev.WriteRumbleReport(0, 0);
                Check(w, $"DualSense rumble output report ({dev.LastWriteMethod})");
                var ls = dev.LastState;
                Line($"[INFO] DualSense state: LX={ls.LX} LY={ls.LY} RX={ls.RX} RY={ls.RY} L2={ls.L2} R2={ls.R2} dpad={ls.Dpad} battery={ls.BatteryPercent}% charging={ls.Charging}");
            }
        }
        catch (Exception ex) { Check(false, "HID: " + ex.Message); }

        // 2) Embedded proxy DLL
        string tmpDir = Path.Combine(Path.GetTempPath(), "DualSenseRumbleHelper_selftest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmpDir);
            string dll = Path.Combine(tmpDir, "xinput1_3.dll");
            File.WriteAllBytes(dll, ProxyInstaller.GetEmbeddedProxy());
            Line($"[INFO] proxy sha256={ProxyInstaller.ProxySha256}");
            IntPtr mod = Win32.LoadLibraryExW(dll, IntPtr.Zero, 0);
            Check(mod != IntPtr.Zero, "proxy LoadLibrary");
            if (mod != IntPtr.Zero)
            {
                string[] names = { "XInputGetState", "XInputSetState", "XInputGetCapabilities", "XInputEnable", "XInputGetDSoundAudioDeviceGuids", "XInputGetBatteryInformation", "XInputGetKeystroke", "RumbleHelperProxyInfo" };
                foreach (var n in names) Check(Win32.GetProcAddress(mod, n) != IntPtr.Zero, "proxy export " + n);
                var info = Marshal.GetDelegateForFunctionPointer<ProxyInfoFn>(Win32.GetProcAddress(mod, "RumbleHelperProxyInfo"));
                info(out uint c0, out _, out uint real);
                Check(real == 1, "proxy real XInput forwarding available");
                Thread.Sleep(1200);
                info(out uint c1, out uint bt, out _);
                Line($"[INFO] proxy DualSense connected={c1} bluetooth={bt} (at load: {c0})");
                var gs = Marshal.GetDelegateForFunctionPointer<GetStateFn>(Win32.GetProcAddress(mod, "XInputGetState"));
                uint rc = gs(0, out var st);
                Line($"[INFO] proxy XInputGetState(0) = {rc} packet={st.dwPacketNumber} buttons=0x{st.Gamepad.wButtons:X4}");
                Check(rc == 0 || rc == Win32.ERROR_DEVICE_NOT_CONNECTED, "proxy XInputGetState returns a valid code");
                Win32.FreeLibrary(mod);
            }
        }
        catch (Exception ex) { Check(false, "proxy: " + ex.Message); }
        finally { try { Directory.Delete(tmpDir, true); } catch { } }

        // 3) ViGEm round trip
        try
        {
            Line($"[INFO] ViGEmBus driver registered={ViGEmClient.IsDriverInstalled()} bus present={ViGEmClient.IsBusPresent()}");
            if (ViGEmClient.IsBusPresent())
            {
                using var vigem = new ViGEmClient();
                vigem.Connect();
                Check(true, "ViGEm connect + version check");
                uint serial = vigem.PlugInXbox360();
                Check(serial > 0, $"ViGEm plug in X360 (serial {serial})");
                try
                {
                    uint? user = vigem.GetXInputUserIndex(serial);
                    Line($"[INFO] XInput user index = {(user.HasValue ? user.Value.ToString() : "?")}");
                    // Press A and look for it through xinput1_4
                    vigem.Submit(serial, new XusbReport { Buttons = XusbReport.A, ThumbLY = 12345 });
                    bool seen = false; uint seenUser = 99;
                    for (int i = 0; i < 40 && !seen; i++)
                    {
                        for (uint u = 0; u < 4; u++)
                        {
                            if (Win32.XInputGetState(u, out var s) == 0 && (s.Gamepad.wButtons & XusbReport.A) != 0 && s.Gamepad.sThumbLY == 12345)
                            { seen = true; seenUser = u; break; }
                        }
                        if (!seen) Thread.Sleep(50);
                    }
                    Check(seen, $"virtual pad visible to XInput with A pressed (user {seenUser})");

                    // Rumble round trip: XInputSetState -> ViGEm notification
                    if (seen)
                    {
                        using var cancel = new ManualResetEvent(false);
                        byte gotL = 0, gotS = 0; bool got = false;
                        var t = new Thread(() =>
                        {
                            for (int i = 0; i < 10 && !got; i++)
                            {
                                if (vigem.WaitForFeedback(serial, cancel, out var l, out var s, out _) && (l != 0 || s != 0)) { gotL = l; gotS = s; got = true; }
                                if (cancel.WaitOne(0)) break;
                            }
                        }) { IsBackground = true };
                        t.Start();
                        Thread.Sleep(100);
                        var vib = new Win32.XINPUT_VIBRATION { wLeftMotorSpeed = 0xFFFF, wRightMotorSpeed = 0x8080 };
                        Win32.XInputSetState(seenUser, ref vib);
                        t.Join(2000);
                        cancel.Set();
                        t.Join(1000);
                        Check(got && gotL == 255 && gotS == 128, $"rumble round trip (large={gotL} small={gotS}, expected 255/128)");
                        vib = default;
                        Win32.XInputSetState(seenUser, ref vib);
                    }
                    vigem.Submit(serial, default);
                }
                finally
                {
                    vigem.Unplug(serial);
                    Line("[INFO] virtual pad unplugged");
                    Thread.Sleep(1500); // PnP removal is asynchronous; avoid a false "virtual pad present" below
                }
            }
            else Line("[SKIP] ViGEmBus not present");
        }
        catch (Exception ex) { Check(false, "ViGEm: " + ex.Message); }

        // 4) Game / tooling detection
        try
        {
            foreach (var g in GameLocator.FindAll())
            {
                var a = ProxyInstaller.Analyze(g.ExePath);
                Line($"[INFO] game: {g} -> proxy name {a.DllName} ({a.Method}); refs: {string.Join(", ", a.AllReferences)}");
                var st = ProxyInstaller.GetState(g.Win64Dir, a.DllName);
                Line($"[INFO]   proxy state: installed={st.Installed} note={st.Note}; folder writable={ProxyInstaller.IsDirectoryWritable(g.Win64Dir)}");
            }
            Line($"[INFO] Steam exe: {GameLocator.GetSteamExe() ?? "-"} running={GameLocator.IsSteamRunning()}");
            Line($"[INFO] HidHide installed={HidHideService.IsInstalled} version={HidHideService.Version ?? "-"} cloak={(HidHideService.IsInstalled ? HidHideService.GetCloakState()?.ToString() : "-")}");
            foreach (var c in ConflictDetector.Detect()) Line($"[INFO] conflict: {c.Title}");
            Check(true, "detection");
        }
        catch (Exception ex) { Check(false, "detection: " + ex.Message); }

        Line(failures == 0 ? "RESULT: ALL PASS" : $"RESULT: {failures} FAILURE(S)");
        string text = sb.ToString();
        if (!string.IsNullOrEmpty(outFile)) File.WriteAllText(outFile, text);
        else System.Windows.Forms.MessageBox.Show(text, "Self-test");
        return failures == 0 ? 0 : 2;
    }
}
