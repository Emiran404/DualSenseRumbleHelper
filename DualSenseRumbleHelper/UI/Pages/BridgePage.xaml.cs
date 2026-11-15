using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DualSenseRumbleHelper.Services;

namespace DualSenseRumbleHelper.UI.Pages;

public partial class BridgePage : Page
{
    private readonly AppState _state = AppState.Instance;

    public BridgePage()
    {
        InitializeComponent();
        HideToggle.IsChecked = _state.Settings.HideWithHidHide;
        Loaded += (_, _) => { _state.StatusChanged += Refresh; _state.Tick += UpdateLive; Loc.Instance.LanguageChanged += Refresh; Refresh(); };
        Unloaded += (_, _) => { _state.StatusChanged -= Refresh; _state.Tick -= UpdateLive; Loc.Instance.LanguageChanged -= Refresh; };
    }

    private Brush B(string key) => (Brush)FindResource(key);

    private void Refresh()
    {
        var s = _state.Status;
        var b = _state.Bridge;

        VigemText.Text = s.ViGEmInstalled ? (s.ViGEmBusPresent ? T("bridge.vigemOk") : T("bridge.vigemNoDevice")) : T("bridge.vigemMissing");
        VigemText.Foreground = B(s.ViGEmBusPresent ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
        VigemButton.Visibility = s.ViGEmInstalled ? Visibility.Collapsed : Visibility.Visible;

        HidHideText.Text = s.HidHideInstalled ? F("bridge.hidhideState", s.HidHideVersion ?? "?", s.HidHideCloak == true ? T("bridge.cloakOn") : T("bridge.cloakOff")) : T("common.notInstalled");
        HidHideButton.Visibility = s.HidHideInstalled ? Visibility.Collapsed : Visibility.Visible;
        HideToggle.IsEnabled = s.HidHideInstalled && !b.IsRunning;

        if (b.IsRunning)
        {
            var c = b.Controller;
            PadText.Text = c != null ? F("bridge.padConnected", c.ModelName, c.Transport, b.LastState.BatteryPercent) + (b.LastState.Charging ? " " + T("bridge.charging") : "") : T("bridge.padWaiting");
            PadText.Foreground = B(c != null ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
        }
        else
        {
            PadText.Text = s.Pad == null ? T("bridge.padNone") : F("bridge.padReady", s.Pad.ModelName, s.Pad.Transport);
            PadText.Foreground = B(s.Pad == null ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");
        }

        var conflicts = s.Conflicts.Where(c => c.Kind != "hidhide").ToList();
        ConflictBar.IsOpen = conflicts.Count > 0 && !b.IsRunning;
        ConflictBar.Message = string.Join("\n", conflicts.Select(c => $"• {c.Title}: {c.Detail}"));

        StartButton.Visibility = b.IsRunning ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = b.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        UpdateLive();
    }

    private void UpdateLive()
    {
        var b = _state.Bridge;
        if (b.IsRunning)
        {
            bool pad = b.ControllerConnected;
            BigTitle.Text = pad ? T("bridge.running") : T("bridge.runningWaiting");
            string slot = b.XInputUserIndex is { } u ? F("bridge.player", u + 1) : "?";
            string rumble = b.MillisSinceLastRumble is { } ms ? F("bridge.secondsAgo", (ms / 1000.0).ToString("0.0")) : T("bridge.notYet");
            BigSub.Text = (pad ? F("bridge.gameSees", slot) : T("bridge.autoWhenConnected")) + (b.HidHideActive ? " " + T("bridge.hidhideActive") : "") + (_state.BridgeAutoStarted ? " " + T("bridge.autoStarted") : "");
            LiveText.Text = F("bridge.live", $"{b.ReportsPerSecond,3}", rumble, b.LastLargeMotor, b.LastSmallMotor);
            MeterLeft.Value = b.LastLargeMotor;
            MeterRight.Value = b.LastSmallMotor;
            BigIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.XboxController24;
        }
        else
        {
            BigTitle.Text = T("bridge.off");
            BigSub.Text = T("bridge.offSub");
            LiveText.Text = "";
            MeterLeft.Value = 0; MeterRight.Value = 0;
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var conflicts = _state.Status.Conflicts.Where(c => c.Kind != "hidhide").ToList();
        if (conflicts.Count > 0)
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = T("bridge.conflictTitle"),
                Content = string.Join("\n", conflicts.Select(c => "• " + c.Title)) + "\n\n" + T("bridge.startAnyway"),
                PrimaryButtonText = T("bridge.startButton"), CloseButtonText = T("common.cancel"),
            };
            if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        }
        _state.Guard(() => _state.StartBridge(false));
        Refresh();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) { _state.Guard(_state.StopBridge); Refresh(); }
    private void Test_Click(object sender, RoutedEventArgs e) => _state.RumbleTest();
    private void HideToggle_Click(object sender, RoutedEventArgs e) { _state.Settings.HideWithHidHide = HideToggle.IsChecked == true; _state.Settings.Save(); }
    private void VigemDownload_Click(object sender, RoutedEventArgs e) => _state.Guard(() => GameLocator.OpenUrl("https://github.com/nefarius/ViGEmBus/releases/latest"));
    private void HidHideDownload_Click(object sender, RoutedEventArgs e) => _state.Guard(() => GameLocator.OpenUrl(HidHideService.DownloadUrl));
}
