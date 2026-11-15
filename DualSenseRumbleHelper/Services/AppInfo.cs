using System.Reflection;

namespace DualSenseRumbleHelper.Services;

/// <summary>Project facts shown in Settings → About.</summary>
public static class AppInfo
{
    public const string Name = "DualSense Rumble Helper";
    public const string Author = "Emiran404";
    public const string RepoUrl = "https://github.com/Emiran404/DualSenseRumbleHelper";
    public const string IssuesUrl = RepoUrl + "/issues";
    public const string ReleasesUrl = RepoUrl + "/releases";
    public const string License = "MIT";
    public const int CopyrightYear = 2026;

    /// <summary>Version from the project file (e.g. "1.3.0"), without the source-control suffix.</summary>
    public static string Version
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            string? v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(v)) v = asm.GetName().Version?.ToString(3);
            int plus = v?.IndexOf('+') ?? -1;
            return plus > 0 ? v![..plus] : v ?? "?";
        }
    }

    /// <summary>When this exe was built (link time of the single-file bundle).</summary>
    public static DateTime BuildDate
    {
        get
        {
            try { return File.GetLastWriteTime(Environment.ProcessPath ?? ""); }
            catch { return DateTime.MinValue; }
        }
    }

    /// <summary>Open-source projects used or required by the app.</summary>
    public static readonly (string Name, string Role, string License, string Url)[] ThirdParty =
    {
        ("WPF UI", "about.tp.wpfui", "MIT", "https://github.com/lepoco/wpfui"),
        ("ViGEmBus", "about.tp.vigem", "BSD-3-Clause", "https://github.com/nefarius/ViGEmBus"),
        ("HidHide", "about.tp.hidhide", "MIT", "https://github.com/nefarius/HidHide"),
        (".NET", "about.tp.dotnet", "MIT", "https://github.com/dotnet/runtime"),
        ("Zig", "about.tp.zig", "MIT", "https://ziglang.org"),
    };
}
