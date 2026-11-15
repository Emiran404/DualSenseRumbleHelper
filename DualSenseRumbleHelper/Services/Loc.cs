using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace DualSenseRumbleHelper.Services;

/// <summary>
/// UI translations. Strings live in embedded JSON files (Resources\Lang\&lt;code&gt;.json, flat "key": "text").
/// English is the fallback for missing keys. XAML binds through <see cref="UI.TExtension"/>, so switching
/// the language updates the open window immediately.
/// To add a language: copy en.json, translate the values and add the code to <see cref="Languages"/>.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"),
        ("tr", "Türkçe"),
        ("de", "Deutsch"),
        ("es", "Español"),
        ("fr", "Français"),
        ("pt-BR", "Português (Brasil)"),
    };

    // Windows display language captured before we change the thread cultures; "auto" must keep reading
    // this, otherwise picking German and then "auto" resolved to German again.
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;
    private static Loc? _instance;
    public static Loc Instance => _instance ??= new Loc();

    private readonly Dictionary<string, string> _fallback;
    private Dictionary<string, string> _current;

    public string Code { get; private set; } = "en";
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Raised after the language changed (pages re-render their code-generated texts).</summary>
    public event Action? LanguageChanged;

    private Loc()
    {
        _fallback = LoadTable("en");
        _current = _fallback;
        string setting = "auto";
        try { setting = AppSettings.Load().Language ?? "auto"; } catch { }
        // Testing aid: "--lang de" overrides the saved language for this run only.
        var argv = Environment.GetCommandLineArgs();
        int li = Array.IndexOf(argv, "--lang");
        if (li >= 0 && li + 1 < argv.Length) setting = argv[li + 1];
        Apply(Resolve(setting), notify: false);
    }

    public string this[string key] =>
        _current.TryGetValue(key, out var v) ? v : _fallback.TryGetValue(key, out v) ? v : key;

    /// <summary>Translated text for a key.</summary>
    public static string T(string key) => Instance[key];

    /// <summary>Translated text with {0}, {1}… placeholders filled in.</summary>
    public static string F(string key, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, Instance[key], args); }
        catch (FormatException) { return Instance[key]; }
    }

    /// <summary>"auto" picks the Windows display language when it is translated, English otherwise.</summary>
    public static string Resolve(string? setting)
    {
        if (!string.IsNullOrEmpty(setting) && setting != "auto" && Languages.Any(l => l.Code == setting)) return setting;
        var ui = CultureInfo.InstalledUICulture;
        foreach (var c in new[] { SystemUiCulture, ui })
        {
            if (Languages.Any(l => l.Code.Equals(c.Name, StringComparison.OrdinalIgnoreCase))) return Languages.First(l => l.Code.Equals(c.Name, StringComparison.OrdinalIgnoreCase)).Code;
            if (c.TwoLetterISOLanguageName == "pt") return "pt-BR";
            var two = Languages.FirstOrDefault(l => l.Code == c.TwoLetterISOLanguageName);
            if (two.Code != null) return two.Code;
        }
        return "en";
    }

    public void SetLanguage(string setting) => Apply(Resolve(setting), notify: true);

    private void Apply(string code, bool notify)
    {
        Code = code;
        _current = code == "en" ? _fallback : LoadTable(code);
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }
        catch { }
        if (!notify) return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke();
    }

    private static Dictionary<string, string> LoadTable(string code)
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Lang.{code}.json");
            if (s != null) return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
        }
        catch { }
        return new();
    }
}
