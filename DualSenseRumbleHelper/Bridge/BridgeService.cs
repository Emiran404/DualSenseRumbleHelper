using System.Drawing;
using DualSenseRumbleHelper.DualSense;
using DualSenseRumbleHelper.Services;
using DualSenseRumbleHelper.ViGEm;

namespace DualSenseRumbleHelper.Bridge;

/// <summary>
/// Mode 2: DualSense (HID) -> virtual Xbox 360 pad (ViGEmBus) -> game; game rumble -> DualSense motors.
/// Handles hot-plug / reconnect and optional HidHide cloaking of the real controller.
/// </summary>
public sealed class BridgeService : IDisposable
{
    private readonly object _sync = new();
    private ViGEmClient? _vigem;
    private uint _serial;
    private DualSenseDevice? _device;
    private Thread? _mainThread, _feedbackThread, _lightbarThread;
    private readonly ManualResetEvent _stop = new(false);
    private bool _hidHideActive;
    private bool? _hidHidePreviousCloak;
    private readonly List<string> _hidHideDevicesAdded = new();
    private long _lastRumbleTick;

    public bool IsRunning { get; private set; }
    public DualSenseInfo? Controller { get; private set; }
    public bool ControllerConnected => _device is { IsRunning: true };
    public int ReportsPerSecond => _device?.ReportsPerSecond ?? 0;
    public byte LastLargeMotor { get; private set; }
    public byte LastSmallMotor { get; private set; }
    public uint? XInputUserIndex { get; private set; }
    public DualSenseState LastState => _device?.LastState ?? default;
    public bool HidHideActive => _hidHideActive;
    /// <summary>Milliseconds since the game last sent a rumble value (null = never).</summary>
    public long? MillisSinceLastRumble => _lastRumbleTick == 0 ? null : Environment.TickCount64 - _lastRumbleTick;

    public event Action<string, bool>? Log;        // message, isError
    public event Action? StatusChanged;

    /// <summary>Motor shaping / lightbar settings; may be replaced at any time while running.</summary>
    public AppSettings Settings { get; set; } = new();

    /// <summary>Re-applies lightbar settings to the connected controller and refreshes the current rumble.</summary>
    public void ApplySettings()
    {
        var dev = _device;
        if (dev == null) return;
        var (l, r) = Settings.ShapeRumble(LastLargeMotor, LastSmallMotor);
        dev.SetRumble(l, r);
        _lightbarDirty = true; // the lightbar loop picks up mode / colour changes
    }

    // ---- lightbar ----------------------------------------------------------

    private volatile bool _lightbarDirty = true;
    private Color? _lastLightbar;
    private static readonly Color FirmwareBlue = Color.FromArgb(255, 0, 0, 255);

    private void LightbarLoop()
    {
        while (!_stop.WaitOne(40))
        {
            var dev = _device;
            if (dev == null || !dev.IsRunning) { _lastLightbar = null; _lightbarDirty = true; continue; }
            var (l, r) = Settings.ShapeRumble(LastLargeMotor, LastSmallMotor);
            Color? c = LightbarEngine.Compute(Settings, dev.LastState, l, r, Environment.TickCount64);
            if (c == null)
            {
                if (_lastLightbar != null)
                {
                    // Leaving a custom mode: hand back a neutral PlayStation blue once.
                    dev.Lightbar = FirmwareBlue;
                    dev.PushOutput();
                    _lastLightbar = null;
                }
                continue;
            }
            if (!_lightbarDirty && _lastLightbar == c) continue;
            dev.Lightbar = c;
            dev.PushOutput();
            _lastLightbar = c;
            _lightbarDirty = false;
        }
    }

    /// <summary>Shows a colour on the controller for a moment (works without the bridge running).</summary>
    public void PreviewLightbar(Color color, int milliseconds = 1500)
    {
        var dev = _device;
        if (dev is { IsRunning: true })
        {
            dev.Lightbar = color;
            dev.PushOutput();
            _lightbarDirty = true; // the loop restores the mode colour afterwards
            return;
        }
        var info = DualSenseLocator.Enumerate().FirstOrDefault()
                   ?? throw new InvalidOperationException(T("err.noPad"));
        using var tmp = new DualSenseDevice(info) { Lightbar = color };
        if (!tmp.WriteRumbleReport(0, 0)) throw new InvalidOperationException(T("err.outputFailed"));
        Thread.Sleep(milliseconds);
    }

    public void Start(bool hideRealController)
    {
        lock (_sync)
        {
            if (IsRunning) return;
            _stop.Reset();

            _vigem?.Dispose();
            var vigem = new ViGEmClient();
            try
            {
                vigem.Connect();
                _serial = vigem.PlugInXbox360();
            }
            catch
            {
                vigem.Dispose();
                throw;
            }
            _vigem = vigem;
            Emit(F("log.padPlugged", _serial));
            try { XInputUserIndex = vigem.GetXInputUserIndex(_serial); } catch { XInputUserIndex = null; }
            if (XInputUserIndex is { } ui) Emit(F("log.xinputIndex", ui + 1));

            if (hideRealController)
            {
                try { EnableHidHide(); }
                catch (Exception ex) { Emit(T("log.hidhideFailed") + " " + ex.Message, true); }
            }

            IsRunning = true;
            _feedbackThread = new Thread(FeedbackLoop) { IsBackground = true, Name = "ViGEm-Feedback" };
            _feedbackThread.Start();
            _mainThread = new Thread(MainLoop) { IsBackground = true, Name = "Bridge-Main" };
            _mainThread.Start();
            _lightbarThread = new Thread(LightbarLoop) { IsBackground = true, Name = "Bridge-Lightbar" };
            _lightbarThread.Start();
        }
        StatusChanged?.Invoke();
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!IsRunning) return;
            IsRunning = false;
            _stop.Set();
        }
        _mainThread?.Join(3000);
        _feedbackThread?.Join(3000);
        _lightbarThread?.Join(1000);
        CloseDevice();
        if (_vigem != null)
        {
            try { _vigem.Submit(_serial, default); } catch { }
            try { _vigem.Unplug(_serial); Emit(T("log.padUnplugged")); } catch (Exception ex) { Emit(T("log.padUnplugFailed") + " " + ex.Message, true); }
            _vigem.Dispose();
            _vigem = null;
        }
        DisableHidHide();
        LastLargeMotor = LastSmallMotor = 0;
        XInputUserIndex = null;
        StatusChanged?.Invoke();
    }

    /// <summary>Pulses both motors briefly to verify the output path. Works with or without the bridge running.</summary>
    public void RumbleTest(byte strength = 180, int milliseconds = 500)
    {
        var (l, r) = Settings.ShapeRumble(strength, strength);
        if (l == 0 && r == 0) { l = strength; r = strength; } // test must be felt even if rumble is disabled in settings
        var dev = _device;
        if (dev is { IsRunning: true })
        {
            dev.SetRumble(l, r);
            Thread.Sleep(milliseconds);
            dev.SetRumble(0, 0);
            return;
        }
        var info = DualSenseLocator.Enumerate().FirstOrDefault()
                   ?? throw new InvalidOperationException(T("err.noPad"));
        using var tmp = new DualSenseDevice(info);
        if (!tmp.WriteRumbleReport(l, r)) throw new InvalidOperationException(T("err.outputFailed"));
        Thread.Sleep(milliseconds);
        tmp.WriteRumbleReport(0, 0);
    }

    private void MainLoop()
    {
        while (!_stop.WaitOne(0))
        {
            DualSenseInfo? info = null;
            try { info = DualSenseLocator.Enumerate().FirstOrDefault(); }
            catch (Exception ex) { Emit(T("log.hidScanFailed") + " " + ex.Message, true); }

            if (info == null)
            {
                if (Controller != null) { Controller = null; StatusChanged?.Invoke(); }
                _stop.WaitOne(1000);
                continue;
            }

            DualSenseDevice dev;
            try
            {
                dev = new DualSenseDevice(info);
            }
            catch (Exception ex)
            {
                Emit(F("log.padOpenFailed", info.Transport) + " " + ex.Message, true);
                _stop.WaitOne(2000);
                continue;
            }

            using var lost = new ManualResetEvent(false);
            dev.Disconnected += reason => { Emit(T("log.padLost") + " " + reason, true); lost.Set(); };
            dev.StateReceived += OnState;
            _device = dev;
            Controller = info;
            Emit(F("log.padConnected", info.ModelName, info.Transport));
            StatusChanged?.Invoke();
            _lastLightbar = null;
            _lightbarDirty = true;
            dev.Start();

            WaitHandle.WaitAny(new WaitHandle[] { _stop, lost });
            CloseDevice();
            Controller = null;
            StatusChanged?.Invoke();
            if (!_stop.WaitOne(0)) _stop.WaitOne(500);
        }
    }

    private void OnState(DualSenseState s)
    {
        var vigem = _vigem;
        if (vigem == null || !IsRunning) return;
        try { vigem.Submit(_serial, Map(s)); }
        catch (Exception ex) { Emit(T("log.vigemReportFailed") + " " + ex.Message, true); }
    }

    private void FeedbackLoop()
    {
        while (!_stop.WaitOne(0))
        {
            var vigem = _vigem;
            if (vigem == null) return;
            bool ok;
            byte large, small, led;
            try { ok = vigem.WaitForFeedback(_serial, _stop, out large, out small, out led); }
            catch (Exception ex) { Emit(T("log.rumbleNotifyFailed") + " " + ex.Message, true); _stop.WaitOne(500); continue; }
            if (!ok) { if (_stop.WaitOne(0)) return; _stop.WaitOne(100); continue; }

            LastLargeMotor = large;
            LastSmallMotor = small;
            _lastRumbleTick = Environment.TickCount64;
            // Xbox: large motor = left, small motor = right (user scaling / swap applied).
            var (l, r) = Settings.ShapeRumble(large, small);
            _device?.SetRumble(l, r);
        }
    }

    private void CloseDevice()
    {
        var dev = _device;
        _device = null;
        if (dev == null) return;
        dev.StateReceived -= OnState;
        try { dev.Dispose(); } catch { }
        try { _vigem?.Submit(_serial, default); } catch { }
    }

    /// <summary>DualSense -> Xbox 360 mapping. Y axes are inverted (HID 0 = up, XInput +32767 = up).</summary>
    public static XusbReport Map(in DualSenseState s)
    {
        var r = new XusbReport
        {
            ThumbLX = AxisX(s.LX), ThumbLY = AxisY(s.LY),
            ThumbRX = AxisX(s.RX), ThumbRY = AxisY(s.RY),
            LeftTrigger = s.L2, RightTrigger = s.R2,
        };
        ushort b = 0;
        switch (s.Dpad)
        {
            case 0: b |= XusbReport.DpadUp; break;
            case 1: b |= XusbReport.DpadUp | XusbReport.DpadRight; break;
            case 2: b |= XusbReport.DpadRight; break;
            case 3: b |= XusbReport.DpadRight | XusbReport.DpadDown; break;
            case 4: b |= XusbReport.DpadDown; break;
            case 5: b |= XusbReport.DpadDown | XusbReport.DpadLeft; break;
            case 6: b |= XusbReport.DpadLeft; break;
            case 7: b |= XusbReport.DpadLeft | XusbReport.DpadUp; break;
        }
        if (s.Cross) b |= XusbReport.A;
        if (s.Circle) b |= XusbReport.B;
        if (s.Square) b |= XusbReport.X;
        if (s.Triangle) b |= XusbReport.Y;
        if (s.L1) b |= XusbReport.LeftShoulder;
        if (s.R1) b |= XusbReport.RightShoulder;
        if (s.L3) b |= XusbReport.LeftThumb;
        if (s.R3) b |= XusbReport.RightThumb;
        if (s.Options) b |= XusbReport.Start;
        if (s.Create) b |= XusbReport.Back;
        if (s.PS) b |= XusbReport.Guide;
        r.Buttons = b;
        return r;
    }

    private static short AxisX(byte v) => (short)(v * 257 - 32768);
    private static short AxisY(byte v) => (short)(32767 - v * 257);

    private void EnableHidHide()
    {
        if (!HidHideService.IsInstalled) throw new InvalidOperationException(T("err.hidhideMissing"));
        string self = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
        HidHideService.RegisterApplication(self);
        _hidHidePreviousCloak = HidHideService.GetCloakState();
        var already = HidHideService.GetHiddenDevices();
        foreach (var id in DualSenseLocator.EnumerateAllHidInstanceIds())
        {
            if (already.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
            HidHideService.HideDevice(id);
            _hidHideDevicesAdded.Add(id);
        }
        HidHideService.SetCloak(true);
        _hidHideActive = true;
        Emit(T("log.hidhideHidden"));
    }

    private void DisableHidHide()
    {
        if (!_hidHideActive) return;
        try
        {
            foreach (var id in _hidHideDevicesAdded) { try { HidHideService.UnhideDevice(id); } catch { } }
            _hidHideDevicesAdded.Clear();
            if (_hidHidePreviousCloak != true) HidHideService.SetCloak(false);
            Emit(T("log.hidhideRestored"));
        }
        catch (Exception ex) { Emit(T("log.hidhideRestoreFailed") + " " + ex.Message, true); }
        _hidHideActive = false;
    }

    private void Emit(string message, bool isError = false) => Log?.Invoke(message, isError);

    public void Dispose()
    {
        Stop();
        _stop.Dispose();
    }
}
