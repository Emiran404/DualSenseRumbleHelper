using System.Windows;
using System.Windows.Threading;
using DualSenseRumbleHelper.Services;
using DualSenseRumbleHelper.UI;

namespace DualSenseRumbleHelper;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Hidden command-line entry points.
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "--proxy-install":
                case "--proxy-uninstall":
                    // Re-launched elevated by ProxyInstaller when the game folder is write protected.
                    return ProxyInstaller.WorkerMain(args);
                case "--selftest":
                    return SelfTest.Run(args.Length > 1 ? args[1] : null);
                case "--symbols":
                    // Build aid: verifies Fluent symbol names used in XAML exist in this Wpf.Ui version.
                    {
                        var missing = args.Skip(1).Where(n => !Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(n, out _)).ToList();
                        File.WriteAllText(Path.Combine(Path.GetTempPath(), "DualSenseRumbleHelper_symbols.txt"), missing.Count == 0 ? "ALL OK" : "MISSING: " + string.Join(", ", missing));
                        return missing.Count == 0 ? 0 : 2;
                    }
            }
        }

        bool allowMulti = args.Contains("--multi", StringComparer.OrdinalIgnoreCase); // testing aid
        using var mutex = new Mutex(true, "DualSenseRumbleHelper.SingleInstance", out bool first);
        if (!first && !allowMulti)
        {
            // Ask the running copy to show itself and wait for its acknowledgement.
            using var ack = new EventWaitHandle(false, EventResetMode.ManualReset, MainWindow.AckEventName);
            MainWindow.ActivateExistingInstance();
            if (ack.WaitOne(3000)) return 0;

            // No answer: the other copy is stuck (e.g. a failed shutdown). Offer to replace it.
            var answer = System.Windows.Forms.MessageBox.Show(
                T("app.stuckInstance"),
                "DualSense Rumble Helper", System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Warning);
            if (answer != System.Windows.Forms.DialogResult.Yes) return 0;
            int self = Environment.ProcessId;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("DualSenseRumbleHelper"))
            {
                if (p.Id == self) continue;
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
            try { if (!mutex.WaitOne(3000)) return 0; }
            catch (AbandonedMutexException) { /* the killed copy held it; we own it now */ }
        }

        bool minimized = args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        var app = new App();
        app.InitializeComponent();
        app.DispatcherUnhandledException += (_, e) => { ReportCrash(e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        try
        {
            AppState.Instance.Initialize();
            var window = new MainWindow(minimized);
            app.MainWindow = window;
            if (!minimized && !AppState.Instance.Settings.StartMinimized) window.Show();
            else window.StartHidden();
            return app.Run();
        }
        catch (Exception ex)
        {
            ReportCrash(ex);
            return 1;
        }
    }

    /// <summary>Writes the exception to %LOCALAPPDATA%\DualSenseRumbleHelper\crash.log and shows a short message.</summary>
    private static void ReportCrash(Exception ex)
    {
        string path = "";
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
            path = Path.Combine(AppSettings.Directory, "crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch { }
        try
        {
            System.Windows.Forms.MessageBox.Show($"{T("app.unexpectedError")}\n\n{ex.GetType().Name}: {ex.Message}\n\n{T("app.details")} {path}", "DualSense Rumble Helper",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
        }
        catch { }
    }
}
