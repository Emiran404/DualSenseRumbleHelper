using System.Windows;
using System.Windows.Controls;
using DualSenseRumbleHelper.Services;

namespace DualSenseRumbleHelper.UI.Pages;

public partial class SteamPage : Page
{
    private readonly AppState _state = AppState.Instance;

    public SteamPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { _state.StatusChanged += Refresh; Loc.Instance.LanguageChanged += Refresh; Refresh(); };
        Unloaded += (_, _) => { _state.StatusChanged -= Refresh; Loc.Instance.LanguageChanged -= Refresh; };
    }

    private void Refresh()
    {
        var games = _state.Games;
        GameText.Text = games.Count == 0
            ? T("steam.gameNotFound")
            : string.Join("  |  ", games.Select(g => $"{g.Source} → {g.RootDir}"));
        ExePathText.Text = _state.CurrentGame?.ExePath ?? T("steam.exeUnknown");

        string? steam = GameLocator.GetSteamExe();
        bool steamGame = games.Any(g => g.Source == "Steam");
        SteamText.Text = steam == null
            ? T("steam.notInstalled")
            : $"{steam} · {(GameLocator.IsSteamRunning() ? T("steam.running") : T("steam.notRunning"))}. " + (steamGame ? T("steam.steamGame") : T("steam.epicGame"));

        var conflicts = _state.Status.Conflicts.ToList();
        if (_state.Bridge.IsRunning) conflicts.Insert(0, new Conflict(T("conflict.ownBridge"), T("conflict.ownBridgeDetail")));
        ConflictBar.IsOpen = conflicts.Count > 0;
        ConflictBar.Message = string.Join("\n", conflicts.Select(c => $"• {c.Title}: {c.Detail}"));
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _state.Guard(_state.OpenGameFolder);
    private void OpenSteam_Click(object sender, RoutedEventArgs e) => _state.Guard(_state.OpenSteam);
    private void AddNonSteam_Click(object sender, RoutedEventArgs e) => _state.Guard(() => _state.OpenSteamUrl("steam://AddNonSteamGame"));
    private void ControllerSettings_Click(object sender, RoutedEventArgs e) => _state.Guard(() => _state.OpenSteamUrl("steam://settings/controller"));
    private void CopyPath_Click(object sender, RoutedEventArgs e) => _state.Guard(_state.CopyExePath);
}
