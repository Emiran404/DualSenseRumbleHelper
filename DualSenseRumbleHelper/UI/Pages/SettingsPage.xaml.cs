using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DualSenseRumbleHelper.Bridge;
using DualSenseRumbleHelper.DualSense;
using DualSenseRumbleHelper.Services;
using DColor = System.Drawing.Color;

namespace DualSenseRumbleHelper.UI.Pages;

public partial class SettingsPage : Page
{
    private readonly AppState _state = AppState.Instance;
    private AppSettings S => _state.Settings;
    private bool _loading = true;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly List<(DColor Color, Border Ring)> _swatches = new();
    private readonly List<RadioButton> _modeButtons = new();

    /// <summary>Preset palette: PlayStation blue first, Rocket League team colours, then a colour wheel.</summary>
    private static readonly (string Name, DColor Color)[] Presets =
    {
        ("color.psBlue", DColor.FromArgb(0, 55, 255)),
        ("color.blueTeam", DColor.FromArgb(0, 120, 255)),
        ("color.orangeTeam", DColor.FromArgb(255, 110, 0)),
        ("color.cyan", DColor.FromArgb(0, 220, 255)),
        ("color.teal", DColor.FromArgb(0, 200, 150)),
        ("color.green", DColor.FromArgb(0, 230, 60)),
        ("color.lime", DColor.FromArgb(170, 255, 0)),
        ("color.yellow", DColor.FromArgb(255, 210, 0)),
        ("color.orange", DColor.FromArgb(255, 140, 0)),
        ("color.red", DColor.FromArgb(255, 20, 30)),
        ("color.pink", DColor.FromArgb(255, 40, 140)),
        ("color.purple", DColor.FromArgb(170, 40, 255)),
        ("color.lavender", DColor.FromArgb(140, 120, 255)),
        ("color.white", DColor.FromArgb(255, 255, 255)),
    };

    public SettingsPage()
    {
        InitializeComponent();
        BuildModeButtons();
        BuildSwatches();
        BuildLanguageCombo();
        LoadValues();
        FillAbout();
        try
        {
            // Use the largest frame of the .ico; the default pick is a small, blurry one.
            var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri("pack://application:,,,/Resources/app.ico"),
                System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            AppIcon.Source = dec.Frames.OrderByDescending(f => f.PixelWidth).First();
        }
        catch { }
        _previewTimer.Tick += (_, _) => UpdatePreview();
        Loaded += (_, _) => { _state.StatusChanged += RefreshGame; Loc.Instance.LanguageChanged += OnLanguageChanged; RefreshGame(); _previewTimer.Start(); };
        Unloaded += (_, _) => { _state.StatusChanged -= RefreshGame; Loc.Instance.LanguageChanged -= OnLanguageChanged; _previewTimer.Stop(); };
    }

    private void LoadValues()
    {
        _loading = true;
        ThemeCombo.SelectedIndex = S.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        StartupToggle.IsChecked = StartupManager.IsEnabled();
        MinimizedToggle.IsChecked = S.StartMinimized;
        CloseToTrayToggle.IsChecked = S.CloseToTray;
        AutoStartToggle.IsChecked = S.AutoStartWithGame;
        AutoStopToggle.IsChecked = S.AutoStopWithGame;
        RumbleToggle.IsChecked = S.RumbleEnabled;
        LeftSlider.Value = S.LeftMotorPercent; LeftValue.Text = $"{S.LeftMotorPercent} %";
        RightSlider.Value = S.RightMotorPercent; RightValue.Text = $"{S.RightMotorPercent} %";
        MinSlider.Value = S.MinimumMotor; MinValue.Text = S.MinimumMotor.ToString();
        SwapToggle.IsChecked = S.SwapMotors;
        BrightSlider.Value = S.LightbarBrightness; BrightValue.Text = $"{S.LightbarBrightness} %";
        foreach (var rb in _modeButtons) rb.IsChecked = (string)rb.Tag == (S.LightbarMode ?? "Off");
        SyncCustomFromColor(S.LightbarColor);
        UpdateModeUi();
        _loading = false;
    }

    private void RefreshGame()
    {
        var g = _state.CurrentGame;
        GameFolderText.Text = g != null ? $"{g.Source} → {g.RootDir}" : T("set.gameNotFound");
    }

    private void Save() { if (!_loading) S.Save(); }

    // ---- general -------------------------------------------------------------

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeCombo.SelectedItem is not ComboBoxItem { Tag: string t } || t == S.Theme) return;
        S.Theme = t; Save();
        MainWindow.ApplyTheme(t);
        _state.AddLog(T("set.theme") + ": " + ((ComboBoxItem)ThemeCombo.SelectedItem).Content);
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        bool on = StartupToggle.IsChecked == true;
        _state.Guard(() => { StartupManager.SetEnabled(on); S.RunAtStartup = on; Save(); _state.AddLog(on ? T("log.startupOn") : T("log.startupOff")); });
    }

    private void Minimized_Click(object sender, RoutedEventArgs e) { S.StartMinimized = MinimizedToggle.IsChecked == true; Save(); }
    private void CloseToTray_Click(object sender, RoutedEventArgs e) { S.CloseToTray = CloseToTrayToggle.IsChecked == true; Save(); }
    private void AutoStart_Click(object sender, RoutedEventArgs e) { S.AutoStartWithGame = AutoStartToggle.IsChecked == true; Save(); }
    private void AutoStop_Click(object sender, RoutedEventArgs e) { S.AutoStopWithGame = AutoStopToggle.IsChecked == true; Save(); }
    private void PickFolder_Click(object sender, RoutedEventArgs e) => _state.Guard(_state.PickGameFolder);
    private void Rescan_Click(object sender, RoutedEventArgs e) { _state.RefreshGames(); _state.RefreshStatusAsync(); _state.AddLog(T("log.rescanned")); }

    // ---- motors --------------------------------------------------------------

    private void Rumble_Click(object sender, RoutedEventArgs e) { S.RumbleEnabled = RumbleToggle.IsChecked == true; Apply(); }
    private void Swap_Click(object sender, RoutedEventArgs e) { S.SwapMotors = SwapToggle.IsChecked == true; Apply(); }

    private void Left_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int v = (int)Math.Round(LeftSlider.Value); LeftValue.Text = $"{v} %";
        if (_loading) return; S.LeftMotorPercent = v; Apply();
    }

    private void Right_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int v = (int)Math.Round(RightSlider.Value); RightValue.Text = $"{v} %";
        if (_loading) return; S.RightMotorPercent = v; Apply();
    }

    private void Min_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int v = (int)Math.Round(MinSlider.Value); MinValue.Text = v.ToString();
        if (_loading) return; S.MinimumMotor = v; Apply();
    }

    private void Apply() { if (_loading) return; _state.ApplyMotorSettings(); }
    private void Test_Click(object sender, RoutedEventArgs e) => _state.RumbleTest();
    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e) => _state.Guard(() => GameLocator.OpenInExplorer(AppSettings.Directory));

    // ---- lightbar: modes -----------------------------------------------------

    private void BuildModeButtons()
    {
        foreach (var (key, title, _) in LightbarEngine.Modes)
        {
            var rb = new RadioButton { Content = title, Tag = key, GroupName = "LightbarMode", Margin = new Thickness(0, 0, 18, 6), MinWidth = 0 };
            rb.Checked += (_, _) =>
            {
                if (_loading) return;
                S.LightbarMode = key;
                S.LightbarEnabled = key != "Off";
                UpdateModeUi();
                Apply();
                _state.AddLog(T("set.lightbar") + ": " + T("lb." + key));
            };
            _modeButtons.Add(rb);
            ModePanel.Children.Add(rb);
        }
    }

    private void UpdateModeUi()
    {
        var mode = LightbarEngine.Modes.FirstOrDefault(m => m.Key == (S.LightbarMode ?? "Off"));
        ModeDescription.Text = mode.Description ?? "";
        bool usesColor = LightbarEngine.UsesColor(S.LightbarMode);
        ColorSection.IsEnabled = usesColor;
        ColorSection.Opacity = usesColor ? 1 : 0.45;
        BrightSlider.IsEnabled = S.LightbarMode != "Off";
    }

    // ---- lightbar: palette ---------------------------------------------------

    private void BuildSwatches()
    {
        foreach (var (name, color) in Presets)
        {
            var fill = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
                Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
            };
            var ring = new Border
            {
                Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Padding = new Thickness(3),
                BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
                Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, ToolTip = T(name), Tag = name,
                Background = Brushes.Transparent, Child = fill,
            };
            ring.MouseLeftButtonUp += (_, _) => SelectColor(color, fromCustom: false);
            _swatches.Add((color, ring));
            SwatchPanel.Children.Add(ring);
        }
    }

    private void SelectColor(DColor c, bool fromCustom)
    {
        S.LightbarColor = c;
        if (!LightbarEngine.UsesColor(S.LightbarMode))
        {
            // Picking a colour while the mode ignores colours switches to a mode that shows it.
            S.LightbarMode = "Static";
            _loading = true;
            foreach (var rb in _modeButtons) rb.IsChecked = (string)rb.Tag == "Static";
            _loading = false;
            UpdateModeUi();
        }
        if (!fromCustom) SyncCustomFromColor(c);
        else UpdateSwatchSelection();
        Apply();
    }

    private void SyncCustomFromColor(DColor c)
    {
        bool was = _loading; _loading = true;
        var (h, s, _) = LightbarEngine.ToHsv(c);
        HueSlider.Value = h;
        SatSlider.Value = s * 100;
        HexBox.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        UpdateSatTrack(h);
        UpdateSwatchSelection();
        _loading = was;
    }

    private void UpdateSwatchSelection()
    {
        var cur = S.LightbarColor;
        var accent = (Brush)FindResource("AccentFillColorDefaultBrush");
        foreach (var (color, ring) in _swatches)
            ring.BorderBrush = color.R == cur.R && color.G == cur.G && color.B == cur.B ? accent : Brushes.Transparent;
    }

    private void UpdateSatTrack(double hue)
    {
        var full = LightbarEngine.FromHsv(hue, 1, 1);
        SatTrack.Background = new LinearGradientBrush(Colors.White, Color.FromRgb(full.R, full.G, full.B), 0);
    }

    private void Custom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized || SatTrack == null) return;
        UpdateSatTrack(HueSlider.Value);
        if (_loading) return;
        var c = LightbarEngine.FromHsv(HueSlider.Value, SatSlider.Value / 100.0, 1);
        HexBox.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        SelectColor(c, fromCustom: true);
    }

    private void Hex_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ApplyHex(); }
    private void Hex_LostFocus(object sender, RoutedEventArgs e) => ApplyHex();

    private void ApplyHex()
    {
        string t = (HexBox.Text ?? "").Trim().TrimStart('#');
        if (t.Length == 6 && int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out int v))
        {
            var c = DColor.FromArgb(255, (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
            if (c.ToArgb() != S.LightbarColor.ToArgb()) SelectColor(c, fromCustom: false);
        }
        else
        {
            var c = S.LightbarColor;
            HexBox.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
    }

    private void Bright_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int v = (int)Math.Round(BrightSlider.Value);
        if (BrightValue != null) BrightValue.Text = $"{v} %";
        if (_loading) return; S.LightbarBrightness = v; Apply();
    }

    // ---- lightbar: preview ---------------------------------------------------

    private void UpdatePreview()
    {
        var b = _state.Bridge;
        var st = b.ControllerConnected ? b.LastState : new DualSenseState { BatteryPercent = 70 };
        var (l, r) = S.ShapeRumble(b.LastLargeMotor, b.LastSmallMotor);
        if (!b.IsRunning && S.LightbarMode == "Rumble")
        {
            // Without the bridge there is no rumble; show a gentle pulse so the mode is understandable.
            double k = 0.5 - 0.5 * Math.Cos((Environment.TickCount64 % 1600) / 1600.0 * 2 * Math.PI);
            l = r = (byte)(k * 255);
        }
        DColor? c = LightbarEngine.Compute(S, st, l, r, Environment.TickCount64);
        if (c is { } col)
        {
            var wc = Color.FromRgb(col.R, col.G, col.B);
            PreviewBar.Background = new SolidColorBrush(wc);
            Glow.Fill = new SolidColorBrush(wc);
            Glow.Visibility = Visibility.Visible;
            PreviewHex.Text = $"#{col.R:X2}{col.G:X2}{col.B:X2}";
        }
        else
        {
            PreviewBar.Background = new SolidColorBrush(Color.FromRgb(40, 44, 52));
            Glow.Visibility = Visibility.Collapsed;
            PreviewHex.Text = T("set.lbOwnLight");
        }
    }

    private void TryOnPad_Click(object sender, RoutedEventArgs e)
    {
        var b = _state.Bridge;
        DColor? c = LightbarEngine.Compute(S, b.ControllerConnected ? b.LastState : new DualSenseState { BatteryPercent = 70 }, 255, 255, Environment.TickCount64);
        _state.PreviewLightbar(c ?? LightbarEngine.Scale(S.LightbarColor, S.LightbarBrightness / 100.0));
    }

    // ---- language ------------------------------------------------------------

    private void BuildLanguageCombo()
    {
        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add(new ComboBoxItem { Content = T("set.languageAuto"), Tag = "auto" });
        foreach (var (code, name) in Loc.Languages)
            LanguageCombo.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        string cur = S.Language ?? "auto";
        LanguageCombo.SelectedItem = LanguageCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == cur) ?? LanguageCombo.Items[0];
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string code } || code == S.Language) return;
        S.Language = code; S.Save();
        Loc.Instance.SetLanguage(code);
        _state.AddLog(T("set.language") + ": " + (code == "auto" ? T("set.languageAuto") : Loc.Languages.First(l => l.Code == code).Name));
    }

    /// <summary>Re-renders the texts this page builds in code (XAML texts update by themselves).</summary>
    private void OnLanguageChanged()
    {
        if (LanguageCombo.Items.Count > 0 && LanguageCombo.Items[0] is ComboBoxItem auto) auto.Content = T("set.languageAuto");
        foreach (var rb in _modeButtons) rb.Content = T("lb." + (string)rb.Tag);
        foreach (var (_, ring) in _swatches) if (ring.Tag is string key) ring.ToolTip = T(key);
        UpdateModeUi();
        RefreshGame();
        FillAbout();
    }

    // ---- about -----------------------------------------------------------------

    private void FillAbout()
    {
        string built = AppInfo.BuildDate > DateTime.MinValue ? AppInfo.BuildDate.ToString("d") : "?";
        VersionText.Text = F("about.version", AppInfo.Version) + " · " + F("about.built", built) + " · .NET " + Environment.Version.ToString(2) + " · x64";
        AuthorText.Text = F("about.author", AppInfo.Author) + " · " + F("about.license", AppInfo.License, AppInfo.CopyrightYear);

        CreditsPanel.Children.Clear();
        foreach (var (name, role, license, url) in AppInfo.ThirdParty)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var link = new Wpf.Ui.Controls.Button
            {
                Content = name, Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent, Padding = new Thickness(6, 3, 6, 3),
                HorizontalAlignment = HorizontalAlignment.Left, ToolTip = url, Cursor = Cursors.Hand,
                Foreground = (Brush)FindResource("AccentTextFillColorPrimaryBrush"),
            };
            link.Click += (_, _) => _state.Guard(() => GameLocator.OpenUrl(url));
            var desc = new Wpf.Ui.Controls.TextBlock { Text = T(role), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 12, 0) };
            desc.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
            var lic = new Wpf.Ui.Controls.TextBlock { Text = license, VerticalAlignment = VerticalAlignment.Center, FontTypography = Wpf.Ui.Controls.FontTypography.Caption };
            lic.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
            Grid.SetColumn(desc, 1); Grid.SetColumn(lic, 2);
            row.Children.Add(link); row.Children.Add(desc); row.Children.Add(lic);
            CreditsPanel.Children.Add(row);
        }
        var thanks = new Wpf.Ui.Controls.TextBlock { Text = T("about.thanks"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 10, 0, 0), FontTypography = Wpf.Ui.Controls.FontTypography.Caption };
        thanks.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
        CreditsPanel.Children.Add(thanks);
    }

    private void Github_Click(object sender, RoutedEventArgs e) => _state.Guard(() => GameLocator.OpenUrl(AppInfo.RepoUrl));
    private void Releases_Click(object sender, RoutedEventArgs e) => _state.Guard(() => GameLocator.OpenUrl(AppInfo.ReleasesUrl));
    private void Issues_Click(object sender, RoutedEventArgs e) => _state.Guard(() => GameLocator.OpenUrl(AppInfo.IssuesUrl));

    /// <summary>Copies version and environment details for a bug report.</summary>
    private void CopyInfo_Click(object sender, RoutedEventArgs e)
    {
        var s = _state.Status;
        string info =
            $"{AppInfo.Name} {AppInfo.Version}\n" +
            $"Windows {Environment.OSVersion.Version} · .NET {Environment.Version} · {Loc.Instance.Code}\n" +
            $"ViGEmBus: {(s.ViGEmInstalled ? (s.ViGEmBusPresent ? "ok" : "installed, no device") : "not installed")}\n" +
            $"HidHide: {(s.HidHideInstalled ? $"v{s.HidHideVersion ?? "?"}, cloak {(s.HidHideCloak == true ? "on" : "off")}" : "not installed")}\n" +
            $"Controller: {(s.Pad != null ? $"{s.Pad.ModelName} ({s.Pad.Transport})" : "none")}\n" +
            $"Mode: {_state.Mode} · Bridge: {(s.BridgeRunning ? "running" : "stopped")}\n" +
            $"Game: {(_state.CurrentGame != null ? _state.CurrentGame.Source : "not found")}";
        _state.Guard(() => { Clipboard.SetText(info); _state.AddLog(T("about.copied")); });
    }
}