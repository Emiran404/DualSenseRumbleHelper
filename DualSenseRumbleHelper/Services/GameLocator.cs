using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DualSenseRumbleHelper.Services;

public sealed record GameInstall(string Source, string RootDir)
{
    public string Win64Dir => Path.Combine(RootDir, "Binaries", "Win64");
    public string ExePath => Path.Combine(Win64Dir, "RocketLeague.exe");
    public bool Exists => File.Exists(ExePath);
    public override string ToString() => $"{Source}: {RootDir}";
}

/// <summary>Finds Rocket League installations (Epic Games and Steam) and launcher locations.</summary>
public static class GameLocator
{
    public const string EpicAppName = "Sugar"; // Rocket League's Epic catalog app name
    public const int SteamAppId = 252950;

    public static List<GameInstall> FindAll()
    {
        var found = new List<GameInstall>();
        void Add(string source, string? root)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            try
            {
                root = Path.GetFullPath(root.Trim().TrimEnd('\\', '/'));
                var gi = new GameInstall(source, root);
                if (gi.Exists && !found.Any(f => string.Equals(f.RootDir, root, StringComparison.OrdinalIgnoreCase)))
                    found.Add(gi);
            }
            catch { }
        }

        // Epic: launcher manifests
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string manifests = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (Directory.Exists(manifests))
        {
            foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    string? app = root.TryGetProperty("AppName", out var a) ? a.GetString() : null;
                    string? name = root.TryGetProperty("DisplayName", out var d) ? d.GetString() : null;
                    string? loc = root.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
                    if (app == EpicAppName || (name?.Contains("Rocket League", StringComparison.OrdinalIgnoreCase) ?? false))
                        Add("Epic Games", loc);
                }
                catch { }
            }
        }
        string launcherInstalled = Path.Combine(programData, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
        if (File.Exists(launcherInstalled))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(launcherInstalled));
                if (doc.RootElement.TryGetProperty("InstallationList", out var list))
                    foreach (var item in list.EnumerateArray())
                    {
                        string? app = item.TryGetProperty("AppName", out var a) ? a.GetString() : null;
                        string? loc = item.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
                        if (app == EpicAppName || (loc?.Contains("rocketleague", StringComparison.OrdinalIgnoreCase) ?? false))
                            Add("Epic Games", loc);
                    }
            }
            catch { }
        }
        // Epic: default folders on every drive
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            Add("Epic Games", Path.Combine(drive.RootDirectory.FullName, "Program Files", "Epic Games", "rocketleague"));
            Add("Epic Games", Path.Combine(drive.RootDirectory.FullName, "Epic Games", "rocketleague"));
        }

        // Steam libraries
        string? steamPath = GetSteamPath();
        if (steamPath != null)
        {
            var libs = new List<string> { steamPath };
            string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                try
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                }
                catch { }
            }
            foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
                Add("Steam", Path.Combine(lib, "steamapps", "common", "rocketleague"));
        }
        return found;
    }

    public static string? GetSteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var p = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return Path.GetFullPath(p);
        }
        catch { }
        string def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(def) ? def : null;
    }

    public static string? GetSteamExe()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var p = key?.GetValue("SteamExe") as string;
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return Path.GetFullPath(p);
        }
        catch { }
        var sp = GetSteamPath();
        if (sp != null && File.Exists(Path.Combine(sp, "steam.exe"))) return Path.Combine(sp, "steam.exe");
        return null;
    }

    public static bool IsSteamRunning() => Process.GetProcessesByName("steam").Length > 0;
    public static bool IsEpicLauncherRunning() => Process.GetProcessesByName("EpicGamesLauncher").Length > 0;
    public static bool IsRocketLeagueRunning() => Process.GetProcessesByName("RocketLeague").Length > 0;

    public static string? GetEpicLauncherExe()
    {
        try
        {
            var p = Process.GetProcessesByName("EpicGamesLauncher").FirstOrDefault();
            if (p != null) { try { return p.MainModule?.FileName; } catch { } }
        }
        catch { }
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Epic Games", "Launcher", "EpicGamesLauncher.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static void OpenInExplorer(string dir)
    {
        if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
