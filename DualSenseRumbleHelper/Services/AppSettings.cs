using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace DualSenseRumbleHelper.Services;

/// <summary>User settings, stored as JSON under %LOCALAPPDATA%\DualSenseRumbleHelper.</summary>
public sealed class AppSettings
{
    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualSenseRumbleHelper");
    public static string FilePath => Path.Combine(Directory, "settings.json");

    /// <summary>Last selected mode (1-3). 0 = decide automatically.</summary>
    public int Mode { get; set; }
    public bool HideWithHidHide { get; set; }
    public bool RunAtStartup { get; set; }
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool AutoStartWithGame { get; set; } = true;
    public bool AutoStopWithGame { get; set; } = true;
    public bool AutoStartNotify { get; set; } = true;

    // Motor settings
    public bool RumbleEnabled { get; set; } = true;
    public int LeftMotorPercent { get; set; } = 100;   // 0..150
    public int RightMotorPercent { get; set; } = 100;  // 0..150
    public bool SwapMotors { get; set; }
    public int MinimumMotor { get; set; }               // 0..60: values below are dropped (anti-buzz)

    // Lightbar
    /// <summary>Kept for older settings files; the source of truth is <see cref="LightbarMode"/>.</summary>
    public bool LightbarEnabled { get; set; }
    public int LightbarArgb { get; set; } = Color.FromArgb(0, 120, 255).ToArgb();
    /// <summary>"Off", "Static", "Breathing", "Rumble", "Battery" or "Rainbow".</summary>
    public string? LightbarMode { get; set; }
    public int LightbarBrightness { get; set; } = 100;   // 5..100 %

    public string? ManualGameRoot { get; set; }

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>"auto" (Windows display language) or a code from <see cref="Loc.Languages"/>.</summary>
    public string Language { get; set; } = "auto";

    [JsonIgnore]
    public Color LightbarColor
    {
        // Always opaque: WinForms controls reject transparent colours and an all-zero value means "unset".
        get => LightbarArgb == 0 ? Color.FromArgb(0, 120, 255) : Color.FromArgb(255, Color.FromArgb(LightbarArgb));
        set => LightbarArgb = Color.FromArgb(255, value).ToArgb();
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                s.LightbarMode ??= s.LightbarEnabled ? "Static" : "Off";
                s.LightbarBrightness = Math.Clamp(s.LightbarBrightness == 0 ? 100 : s.LightbarBrightness, 5, 100);
                return s;
            }
        }
        catch { }
        return new AppSettings { LightbarMode = "Off" };
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>Applies left/right scaling, swap and threshold to a raw Xbox rumble pair.</summary>
    public (byte Left, byte Right) ShapeRumble(byte large, byte small)
    {
        if (!RumbleEnabled) return (0, 0);
        int l = large * Math.Clamp(LeftMotorPercent, 0, 200) / 100;
        int r = small * Math.Clamp(RightMotorPercent, 0, 200) / 100;
        if (l < MinimumMotor) l = 0;
        if (r < MinimumMotor) r = 0;
        l = Math.Clamp(l, 0, 255); r = Math.Clamp(r, 0, 255);
        return SwapMotors ? ((byte)r, (byte)l) : ((byte)l, (byte)r);
    }
}

/// <summary>Per-user "run at logon" entry (HKCU, no admin rights needed).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DualSenseRumbleHelper";

    public static string Command => $"\"{Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName}\" --minimized";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true) ?? throw new InvalidOperationException(T("err.registry"));
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, false);
    }
}
