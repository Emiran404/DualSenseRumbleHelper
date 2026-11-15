using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DualSenseRumbleHelper.Services;

/// <summary>Thin wrapper over HidHideCLI.exe (ships with HidHide, works without elevation).</summary>
public static class HidHideService
{
    public const string DownloadUrl = "https://github.com/nefarius/HidHide/releases/latest";

    public static string? CliPath
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Nefarius Software Solutions e.U.\HidHide");
                if (key?.GetValue("Path") is string p)
                {
                    string cli = Path.Combine(p, "x64", "HidHideCLI.exe");
                    if (File.Exists(cli)) return cli;
                }
            }
            catch { }
            string def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nefarius Software Solutions", "HidHide", "x64", "HidHideCLI.exe");
            return File.Exists(def) ? def : null;
        }
    }

    public static bool IsDriverRegistered()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\HidHide");
            return key != null;
        }
        catch { return false; }
    }

    public static bool IsInstalled => CliPath != null && IsDriverRegistered();

    public static string? Version
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Nefarius Software Solutions e.U.\HidHide");
                return key?.GetValue("Version") as string;
            }
            catch { return null; }
        }
    }

    public static bool? GetCloakState()
    {
        string out_ = Run("--cloak-state");
        if (out_.Contains("--cloak-on")) return true;
        if (out_.Contains("--cloak-off")) return false;
        return null;
    }

    public static void SetCloak(bool on) => Run(on ? "--cloak-on" : "--cloak-off");
    public static void RegisterApplication(string exePath) => Run($"--app-reg \"{exePath}\"");
    public static void UnregisterApplication(string exePath) => Run($"--app-unreg \"{exePath}\"");
    public static void HideDevice(string instanceId) => Run($"--dev-hide \"{instanceId}\"");
    public static void UnhideDevice(string instanceId) => Run($"--dev-unhide \"{instanceId}\"");

    public static List<string> GetHiddenDevices() => ParseQuoted(Run("--dev-list"));
    public static List<string> GetWhitelistedApplications() => ParseQuoted(Run("--app-list"));

    private static List<string> ParseQuoted(string text) =>
        Regex.Matches(text, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();

    private static string Run(string arguments)
    {
        string cli = CliPath ?? throw new InvalidOperationException(T("err.hidhideCli"));
        var psi = new ProcessStartInfo(cli, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(T("err.hidhideCliStart"));
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } throw new TimeoutException(T("err.hidhideCliTimeout")); }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"HidHideCLI {arguments} failed (code {p.ExitCode}): {stderr.Trim()} {stdout.Trim()}".Trim());
        return stdout;
    }
}
