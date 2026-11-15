using System.Windows;
using System.Windows.Controls;
using DualSenseRumbleHelper.Services;

namespace DualSenseRumbleHelper.UI.Pages;

public partial class ProxyPage : Page
{
    private readonly AppState _state = AppState.Instance;
    private ProxyAnalysis? _analysis;
    private bool _filling;

    public ProxyPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { _state.StatusChanged += Refresh; Loc.Instance.LanguageChanged += Refresh; FillGames(); Refresh(); };
        Unloaded += (_, _) => { _state.StatusChanged -= Refresh; Loc.Instance.LanguageChanged -= Refresh; };
    }

    private void FillGames()
    {
        _filling = true;
        GameCombo.Items.Clear();
        foreach (var g in _state.Games) GameCombo.Items.Add(g);
        GameCombo.SelectedItem = _state.CurrentGame;
        _filling = false;
    }

    private void Refresh()
    {
        if (GameCombo.Items.Count != _state.Games.Count) FillGames();
        var g = _state.CurrentGame;
        bool accepted = AcceptToggle.IsChecked == true;
        if (g == null)
        {
            AnalysisText.Text = T("proxy.noGame");
            StateText.Text = T("proxy.stateNoGame");
            StateSub.Text = "";
            InstallButton.IsEnabled = UninstallButton.IsEnabled = false;
            return;
        }
        try
        {
            _analysis = ProxyInstaller.Analyze(g.ExePath);
            string refs = _analysis.AllReferences.Count > 0 ? string.Join(", ", _analysis.AllReferences.Select(ProxyInstaller.DescribeReference)) : T("common.none");
            AnalysisText.Text = F("proxy.analysis", _analysis.DllName, ProxyInstaller.DescribeMethod(_analysis.Method), refs);
            var st = ProxyInstaller.GetState(g.Win64Dir, _analysis.DllName);
            bool writable = ProxyInstaller.IsDirectoryWritable(g.Win64Dir);
            StateText.Text = F("proxy.state", st.Note);
            StateSub.Text = (st.OriginalBackedUp ? T("proxy.backedUp") + " " : "") + (writable ? T("proxy.writable") : T("proxy.protected"));
            InstallButton.IsEnabled = accepted && !st.Installed;
            UninstallButton.IsEnabled = accepted && st.Installed;
        }
        catch (Exception ex)
        {
            AnalysisText.Text = T("proxy.analysisError") + " " + ex.Message;
            InstallButton.IsEnabled = UninstallButton.IsEnabled = false;
        }
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => Refresh();

    private void GameCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        if (GameCombo.SelectedItem is GameInstall g) { _state.CurrentGame = g; Refresh(); }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var g = _state.CurrentGame; var a = _analysis;
        if (g == null || a == null) return;
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = T("proxy.confirmTitle"),
            Content = F("proxy.confirm", Path.Combine(g.Win64Dir, a.DllName), a.DllName, ProxyInstaller.BackupNameFor(a.DllName)),
            PrimaryButtonText = T("proxy.installButton"), CloseButtonText = T("common.cancel"),
        };
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
        _state.Guard(() => _state.InstallProxy(a));
        Refresh();
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e) { _state.Guard(() => _state.UninstallProxy()); Refresh(); }
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _state.Guard(_state.OpenGameFolder);
}
