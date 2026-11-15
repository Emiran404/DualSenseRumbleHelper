using System.Windows;
using System.Windows.Controls;
using DualSenseRumbleHelper.Services;

namespace DualSenseRumbleHelper.UI.Pages;

public partial class LogPage : Page
{
    private readonly AppState _state = AppState.Instance;

    public LogPage()
    {
        InitializeComponent();
        DataContext = _state.Log;
        Loaded += (_, _) => { _state.Logged += OnLogged; ScrollToEnd(); };
        Unloaded += (_, _) => _state.Logged -= OnLogged;
    }

    private void OnLogged(LogEntry e) => ScrollToEnd();

    private void ScrollToEnd()
    {
        if (List.Items.Count > 0) List.ScrollIntoView(List.Items[List.Items.Count - 1]);
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => _state.Guard(() => Clipboard.SetText(string.Join(Environment.NewLine, _state.Log.Select(l => l.Line))));
    private void Clear_Click(object sender, RoutedEventArgs e) => _state.Log.Clear();
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _state.Guard(() => GameLocator.OpenInExplorer(AppSettings.Directory));
}
