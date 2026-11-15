using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DualSenseRumbleHelper.Bridge;
using DualSenseRumbleHelper.DualSense;
using DualSenseRumbleHelper.Services;
using DualSenseRumbleHelper.ViGEm;

namespace DualSenseRumbleHelper.UI;

public sealed record LogEntry(DateTime Time, string Text, bool IsError)
{
    public string Line => $"[{Time:HH:mm:ss}] {(IsError ? T("log.errorPrefix") + " " : "")}{Text}";
}

/// <summary>Everything the UI pages observe: the environment (drivers, pad, game), the bridge and the settings.</summary>
public sealed record StatusSnapshot(
    bool ViGEmInstalled, bool ViGEmBusPresent, bool HidHideInstalled, bool? HidHideCloak, string? HidHideVersion,
    DualSenseInfo? Pad, bool GameRunning, IReadOnlyList<Conflict> Conflicts, bool BridgeRunning, bool BridgePadConnected);

/// <summary>Application-wide state shared by all pages (single instance per process).</summary>
public sealed class AppState
{
    public static AppState Instance { get; } = new();

    public AppSettings Settings { get; } = AppSettings.Load();
    public BridgeService Bridge { get; } = new();
    public ObservableCollection<LogEntry> Log { get; } = new();
    public List<GameInstall> Games { get; private set; } = new();
    public GameInstall? CurrentGame { get; set; }
    public StatusSnapshot Status { get; private set; } = new(false, false, false, null, null, null, false, Array.Empty<Conflict>(), false, false);
    public bool BridgeAutoStarted { get; private set; }
    public int Mode { get => Settings.Mode; set { if (Settings.Mode != value) { Settings.Mode = value; Settings.Save(); } } }

    /// <summary>Raised on the UI thread whenever Status / bridge state changed.</summary>
    public event Action? StatusChanged;
    /// <summary>Raised on the UI thread for every log line (also used for the footer).</summary>
    public event Action<LogEntry>? Logged;
    /// <summary>Raised on the UI thread once per second for live meters.</summary>
    public event Action? Tick;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Dispatcher? _dispatcher;
    private bool _gameWasRunning;
    private int _tick;
    private bool _refreshing;

    private AppState() { }

    public void Initialize()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        Bridge.Settings = Settings;
        Bridge.Log += (m, e) => AddLog(m, e);
        Bridge.StatusChanged += () => Post(() => { RefreshStatusAsync(); });
        if (Settings.Mode is < 1 or > 3) Settings.Mode = ViGEmClient.IsDriverInstalled() ? 2 : 1; // Mode 3 is never the default
        AddLog(F("log.started", AppInfo.Version, AppSettings.FilePath));
        RefreshGames();
        _gameWasRunning = GameLocator.IsRocketLeagueRunning();
        RefreshStatusAsync();
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        if (_gameWasRunning && Settings.AutoStartWithGame && Settings.Mode == 2 && !Bridge.IsRunning)
            Guard(() => StartBridge(true));
    }

    public void Shutdown()
    {
        _timer.Stop();
        try { Bridge.Stop(); } catch { }   // zeroes the motors and unplugs the virtual pad
        Bridge.Dispose();
        Settings.Save();
    }

    // ------------------------------------------------------------------ logging

    public void AddLog(string text, bool isError = false)
    {
        var entry = new LogEntry(DateTime.Now, text, isError);
        Post(() =>
        {
            Log.Add(entry);
            if (Log.Count > 500) Log.RemoveAt(0);
            Logged?.Invoke(entry);
        });
    }

    public void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { AddLog(ex.Message, true); }
    }

    private void Post(Action a)
    {
        var d = _dispatcher;
        if (d == null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }

    // ------------------------------------------------------------------ environment

    public void RefreshGames()
    {
        var games = GameLocator.FindAll();
        if (Settings.ManualGameRoot is { } manual)
        {
            var gi = new GameInstall(T("game.manual"), manual);
            if (gi.Exists && !games.Any(g => string.Equals(g.RootDir, manual, StringComparison.OrdinalIgnoreCase))) games.Insert(0, gi);
        }
        Games = games;
        if (CurrentGame == null || !games.Any(g => g.RootDir.Equals(CurrentGame.RootDir, StringComparison.OrdinalIgnoreCase)))
            CurrentGame = games.FirstOrDefault();
    }

    /// <summary>Collects driver / pad / game state on a worker thread (HidHideCLI and HID scans are slow) and publishes it.</summary>
    public void RefreshStatusAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        Task.Run(() =>
        {
            StatusSnapshot snap;
            try
            {
                bool vigem = ViGEmClient.IsDriverInstalled();
                bool bus = vigem && ViGEmClient.IsBusPresent();
                bool hh = HidHideService.IsInstalled;
                bool? cloak = null; string? ver = null;
                if (hh) { try { cloak = HidHideService.GetCloakState(); ver = HidHideService.Version; } catch { } }
                DualSenseInfo? pad = Bridge.IsRunning ? Bridge.Controller : null;
                if (!Bridge.IsRunning) { try { pad = DualSenseLocator.Enumerate().FirstOrDefault(); } catch { } }
                List<Conflict> conflicts; try { conflicts = ConflictDetector.Detect(); } catch { conflicts = new(); }
                snap = new StatusSnapshot(vigem, bus, hh, cloak, ver, pad, GameLocator.IsRocketLeagueRunning(), conflicts, Bridge.IsRunning, Bridge.ControllerConnected);
            }
            catch (Exception ex)
            {
                AddLog(T("log.statusFailed") + " " + ex.Message, true);
                snap = Status with { BridgeRunning = Bridge.IsRunning, BridgePadConnected = Bridge.ControllerConnected };
            }
            Post(() => { Status = snap; _refreshing = false; StatusChanged?.Invoke(); });
        });
    }

    private void OnTick()
    {
        _tick++;
        Tick?.Invoke();

        bool running = GameLocator.IsRocketLeagueRunning();
        if (running && !_gameWasRunning)
        {
            AddLog(T("log.gameStarted"));
            if (Settings.AutoStartWithGame && Settings.Mode == 2 && !Bridge.IsRunning) Guard(() => StartBridge(true));
            RefreshStatusAsync();
        }
        else if (!running && _gameWasRunning)
        {
            AddLog(T("log.gameStopped"));
            if (Settings.AutoStopWithGame && Bridge.IsRunning && BridgeAutoStarted) Guard(StopBridge);
            RefreshStatusAsync();
        }
        _gameWasRunning = running;

        if (_tick % 5 == 0) RefreshStatusAsync();
    }

    // ------------------------------------------------------------------ bridge

    public event Action<string>? AutoStartNotification;

    public void StartBridge(bool automatic)
    {
        if (Bridge.IsRunning) return;
        if (!ViGEmClient.IsDriverInstalled())
            throw new InvalidOperationException(T("err.vigemMissing"));
        bool hide = Settings.HideWithHidHide && HidHideService.IsInstalled;
        Bridge.Settings = Settings;
        Bridge.Start(hide);
        BridgeAutoStarted = automatic;
        AddLog(automatic ? T("log.bridgeAutoStarted") : T("log.bridgeStarted"));
        if (automatic && Settings.AutoStartNotify) AutoStartNotification?.Invoke(T("log.bridgeAutoStarted"));
        RefreshStatusAsync();
    }

    public void StopBridge()
    {
        Bridge.Stop();
        BridgeAutoStarted = false;
        RefreshStatusAsync();
    }

    public void RumbleTest()
    {
        Task.Run(() => Guard(() => { Bridge.RumbleTest(); AddLog(T("log.rumbleTest")); }));
    }

    public void ApplyMotorSettings()
    {
        Settings.Save();
        Guard(Bridge.ApplySettings);
    }

    // ------------------------------------------------------------------ helpers used by pages

    public void OpenSteam()
    {
        string exe = GameLocator.GetSteamExe() ?? throw new InvalidOperationException(T("err.steamMissing"));
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        AddLog(T("log.steamStarted") + " " + exe);
    }

    public void OpenSteamUrl(string url)
    {
        if (GameLocator.GetSteamExe() == null) throw new InvalidOperationException(T("err.steamMissing"));
        GameLocator.OpenUrl(url);
        AddLog(T("log.steamUrl") + " " + url);
    }

    public void OpenGameFolder()
    {
        var g = CurrentGame ?? throw new InvalidOperationException(T("err.gameNotFound"));
        GameLocator.OpenInExplorer(g.Win64Dir);
    }

    public void CopyExePath()
    {
        var g = CurrentGame ?? throw new InvalidOperationException(T("err.gameNotFound"));
        Clipboard.SetText(g.ExePath);
        AddLog(T("log.pathCopied") + " " + g.ExePath);
    }

    public void PickGameFolder()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = T("dlg.pickFolder"), UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        string root = dlg.SelectedPath;
        if (File.Exists(Path.Combine(root, "RocketLeague.exe"))) root = Path.GetFullPath(Path.Combine(root, "..", ".."));
        else if (File.Exists(Path.Combine(root, "Win64", "RocketLeague.exe"))) root = Path.GetFullPath(Path.Combine(root, ".."));
        var gi = new GameInstall(T("game.manual"), root);
        if (!gi.Exists) throw new InvalidOperationException(T("err.noExeInFolder"));
        Settings.ManualGameRoot = root;
        Settings.Save();
        RefreshGames();
        CurrentGame = gi;
        RefreshStatusAsync();
        AddLog(T("log.folderPicked") + " " + root);
    }

    /// <summary>Shows the colour on the controller for a moment (bridge running or not).</summary>
    public void PreviewLightbar(System.Drawing.Color color)
    {
        Task.Run(() => Guard(() => { Bridge.PreviewLightbar(color); AddLog($"{T("log.lightbarPreview")} #{color.R:X2}{color.G:X2}{color.B:X2}"); }));
    }

    public string InstallProxy(ProxyAnalysis analysis)
    {
        var g = CurrentGame ?? throw new InvalidOperationException(T("err.noGameSelected"));
        if (GameLocator.IsRocketLeagueRunning()) throw new InvalidOperationException(T("err.gameRunning"));
        string msg = ProxyInstaller.Install(g.Win64Dir, analysis.DllName);
        AddLog(msg);
        return msg;
    }

    public string UninstallProxy()
    {
        var g = CurrentGame ?? throw new InvalidOperationException(T("err.noGameSelected"));
        if (GameLocator.IsRocketLeagueRunning()) throw new InvalidOperationException(T("err.gameRunning"));
        string msg = ProxyInstaller.Uninstall(g.Win64Dir);
        AddLog(msg);
        return msg;
    }
}
