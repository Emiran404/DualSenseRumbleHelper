using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DualSenseRumbleHelper.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DualSenseRumbleHelper.UI;

public partial class MainWindow : FluentWindow
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private static readonly uint WM_SHOW_INSTANCE = RegisterWindowMessageW("DualSenseRumbleHelper.ShowInstance");
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    public static void ActivateExistingInstance() => PostMessageW(HWND_BROADCAST, WM_SHOW_INSTANCE, IntPtr.Zero, IntPtr.Zero);

    private readonly AppState _state = AppState.Instance;
    private bool _exiting;
    private bool _navigatedOnce;

    public MainWindow(bool startMinimized)
    {
        InitializeComponent();
        _instance = this;
        ApplyTheme(_state.Settings.Theme);
        PreviewMouseWheel += OnPreviewMouseWheel;

        _state.StatusChanged += UpdateFooter;
        _state.Logged += e => { FooterText.Text = e.Line; FooterText.Foreground = (Brush)FindResource(e.IsError ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"); };
        _state.AutoStartNotification += _ => { /* tray balloons are not available with the Fluent tray icon; the footer and log carry the message */ };

        Loaded += (_, _) =>
        {
            if (!_navigatedOnce)
            {
                _navigatedOnce = true;
                int page = _state.Mode;
                // Testing aid: "--page 4" opens a given page at start-up.
                var argv = Environment.GetCommandLineArgs();
                int pi = Array.IndexOf(argv, "--page");
                if (pi >= 0 && pi + 1 < argv.Length && int.TryParse(argv[pi + 1], out int pg) && pg is >= 1 and <= 5) page = pg;
                // Navigate once the NavigationView template is fully applied.
                Dispatcher.BeginInvoke(new Action(() => NavigateToMode(page)), System.Windows.Threading.DispatcherPriority.Loaded);
                if (argv.Contains("--scrolltest"))
                {
                    var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    t.Tick += (_, _) => { t.Stop(); ScrollTest(); };
                    t.Start();
                }
            }
            UpdateFooter();
            Tray.Register();
            DisableNavigationScrollWrapper();
        };
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) HideToTray(); };
        Closing += OnClosing;
        Loc.Instance.LanguageChanged += () => { HeaderText.Text = _currentTag != null ? T("header." + _currentTag) : ""; UpdateFooter(); };
    }

    private string? _currentTag;

    private static MainWindow? _instance;
    private static bool _watching;

    /// <summary>
    /// Applies the theme. Only the "System" choice follows Windows; a manual Light/Dark choice stops the
    /// system theme watcher, which otherwise switched the app back to the Windows theme on its own.
    /// </summary>
    public static void ApplyTheme(string theme)
    {
        var w = _instance;
        if (theme is "Light" or "Dark")
        {
            if (_watching && w != null) { SystemThemeWatcher.UnWatch(w); _watching = false; }
            ApplicationThemeManager.Apply(theme == "Light" ? ApplicationTheme.Light : ApplicationTheme.Dark, WindowBackdropType.Mica, true);
        }
        else
        {
            ApplicationThemeManager.ApplySystemTheme(true);
            if (!_watching && w != null) { SystemThemeWatcher.Watch(w, WindowBackdropType.Mica, true); _watching = true; }
        }
    }

    // ---- scrolling ------------------------------------------------------------

    /// <summary>
    /// NavigationView wraps every page in its own scroll viewer; the pages have one too. With two nested
    /// scroll viewers the inner one never gets a finite height, so the wheel only worked in some places.
    /// Turning the wrapper off leaves exactly one scroll viewer per page.
    /// </summary>
    private void DisableNavigationScrollWrapper()
    {
        foreach (var presenter in FindDescendants<NavigationViewContentPresenter>(Nav))
            presenter.SetValue(NavigationViewContentPresenter.IsDynamicScrollViewerEnabledProperty, false);
    }

    /// <summary>
    /// Fallback: routes the mouse wheel to the innermost scroll viewer under the cursor that can still move
    /// in that direction (sliders, cards and buttons otherwise swallow or ignore the wheel).
    /// </summary>
    private void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d) return;
        for (var cur = d; cur != null; cur = ParentOf(cur))
        {
            if (cur is System.Windows.Controls.ComboBox { IsDropDownOpen: true }) return;
            if (cur is System.Windows.Controls.ScrollViewer sv && sv.ScrollableHeight > 0)
            {
                bool up = e.Delta > 0;
                if ((up && sv.VerticalOffset <= 0) || (!up && sv.VerticalOffset >= sv.ScrollableHeight)) continue;
                sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta * 0.5);
                e.Handled = true;
                return;
            }
        }
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var x in FindDescendants<T>(child)) yield return x;
        }
    }

    /// <summary>Testing aid: scrolls the visible page to the bottom through the same wheel path.</summary>
    public void ScrollTest()
    {
        var sv = FindDescendants<System.Windows.Controls.ScrollViewer>(Nav).Where(s => s.IsVisible && s.ScrollableHeight > 0).LastOrDefault();
        _state.AddLog(sv == null ? "Scroll test: nothing to scroll" : $"Scroll test: height {sv.ScrollableHeight:0}");
        sv?.ScrollToBottom();
    }

    /// <summary>Used when the app starts minimized: create the handle without showing the window so the tray icon works.</summary>
    public void StartHidden()
    {
        ShowInTaskbar = false;
        WindowState = WindowState.Minimized;
        Show();
        Hide();
        Tray.Register();
        _state.AddLog(T("log.startedInTray"));
    }

    public void NavigateToMode(int mode)
    {
        Type page = mode switch { 1 => typeof(Pages.SteamPage), 3 => typeof(Pages.ProxyPage), 4 => typeof(Pages.SettingsPage), 5 => typeof(Pages.LogPage), _ => typeof(Pages.BridgePage) };
        Nav.Navigate(page);
    }

    private void Nav_SelectionChanged(NavigationView sender, RoutedEventArgs args)
    {
        if (sender.SelectedItem is NavigationViewItem item)
        {
            if (item.Tag is string tag && int.TryParse(tag, out int mode))
            {
                _currentTag = tag;
                HeaderText.Text = T("header." + tag);
                if (mode is >= 1 and <= 3) _state.Mode = mode;
            }
        }
    }

    private void UpdateFooter()
    {
        var s = _state.Status;
        FooterPad.Text = s.Pad != null ? $"{s.Pad.ModelName} · {s.Pad.Transport}" : T("footer.noPad");
        DotPad.Fill = (Brush)FindResource(s.Pad != null ? "SystemFillColorSuccessBrush" : "TextFillColorDisabledBrush");
        FooterBridge.Text = s.BridgeRunning ? (s.BridgePadConnected ? T("footer.bridgeOn") : T("footer.bridgeWaiting")) : T("footer.bridgeOff");
        DotBridge.Fill = (Brush)FindResource(s.BridgeRunning ? (s.BridgePadConnected ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush") : "TextFillColorDisabledBrush");
        TrayStart.IsEnabled = !s.BridgeRunning;
        TrayStop.IsEnabled = s.BridgeRunning;
        Tray.TooltipText = s.BridgeRunning ? "DualSense Rumble Helper · " + T("tray.tooltipRunning") : "DualSense Rumble Helper";
    }

    // ---- tray ---------------------------------------------------------------

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
    }

    private void Tray_LeftClick(Wpf.Ui.Tray.Controls.NotifyIcon sender, RoutedEventArgs e) => ShowFromTray();
    private void TrayShow_Click(object sender, RoutedEventArgs e) => ShowFromTray();
    private void TrayStart_Click(object sender, RoutedEventArgs e) => _state.Guard(() => _state.StartBridge(false));
    private void TrayStop_Click(object sender, RoutedEventArgs e) => _state.Guard(_state.StopBridge);
    private async void TrayExit_Click(object sender, RoutedEventArgs e) => await ExitAsync();

    public async Task ExitAsync()
    {
        if (_state.Bridge.IsRunning && GameLocator.IsRocketLeagueRunning())
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = T("exit.title"),
                Content = T("exit.confirm"),
                PrimaryButtonText = T("exit.button"), CloseButtonText = T("common.cancel"),
            };
            if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        }
        _exiting = true;
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting && _state.Settings.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            if (_state.Bridge.IsRunning) _state.AddLog(T("log.bridgeKeepsRunning"));
            return;
        }
        // Clean up (motors off, virtual pad unplugged, settings saved), then leave the process for sure.
        // A plain Application.Shutdown() can throw late inside WPF and leave an invisible process behind,
        // which then blocks every new launch through the single-instance mutex.
        try { Tray.Unregister(); } catch { }
        try { _state.Shutdown(); } catch { }
        Environment.Exit(0);
    }

    public const string AckEventName = "DualSenseRumbleHelper.ShowAck";

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SHOW_INSTANCE)
        {
            // Tell the new launch that this instance is alive and has shown itself.
            try { if (EventWaitHandle.TryOpenExisting(AckEventName, out var ack)) { ack.Set(); ack.Dispose(); } } catch { }
            ShowFromTray();
            handled = true;
        }
        return IntPtr.Zero;
    }
}
