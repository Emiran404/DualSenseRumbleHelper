using System.Drawing;
using DualSenseRumbleHelper.DualSense;
using DualSenseRumbleHelper.Services;

namespace DualSenseRumbleHelper.Bridge;

/// <summary>Computes the lightbar colour for the selected lightbar mode.</summary>
public static class LightbarEngine
{
    private static readonly string[] ModeKeys = { "Off", "Static", "Breathing", "Rumble", "Battery", "Rainbow" };

    /// <summary>Modes with their title and description in the current UI language.</summary>
    public static (string Key, string Title, string Description)[] Modes =>
        ModeKeys.Select(k => (k, T($"lb.{k}"), T($"lb.{k}.desc"))).ToArray();

    /// <summary>Whether the mode uses the user-picked colour.</summary>
    public static bool UsesColor(string? mode) => mode is "Static" or "Breathing" or "Rumble";

    /// <summary>null = leave the lightbar to the controller firmware.</summary>
    public static Color? Compute(AppSettings s, DualSenseState state, byte rumbleLeft, byte rumbleRight, long timeMs)
    {
        double bright = Math.Clamp(s.LightbarBrightness, 5, 100) / 100.0;
        Color baseColor = s.LightbarColor;
        switch (s.LightbarMode)
        {
            case "Static":
                return Scale(baseColor, bright);
            case "Breathing":
            {
                double phase = (timeMs % 3000) / 3000.0 * 2 * Math.PI;
                double k = 0.15 + 0.85 * (0.5 - 0.5 * Math.Cos(phase));
                return Scale(baseColor, bright * k);
            }
            case "Rumble":
            {
                double intensity = Math.Max(rumbleLeft, rumbleRight) / 255.0;
                return Scale(baseColor, bright * (0.15 + 0.85 * intensity));
            }
            case "Battery":
            {
                double hue = Math.Clamp(state.BatteryPercent, 0, 100) * 1.2; // 0 = red, 120 = green
                double k = 1.0;
                if (state.Charging) k = 0.35 + 0.65 * (0.5 - 0.5 * Math.Cos((timeMs % 2000) / 2000.0 * 2 * Math.PI));
                return FromHsv(hue, 1, bright * k);
            }
            case "Rainbow":
                return FromHsv((timeMs % 6000) / 6000.0 * 360.0, 1, bright);
            default:
                return null;
        }
    }

    public static Color Scale(Color c, double k)
    {
        k = Math.Clamp(k, 0, 1);
        return Color.FromArgb(255, (int)Math.Round(c.R * k), (int)Math.Round(c.G * k), (int)Math.Round(c.B * k));
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromArgb(255, (int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }

    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }
}
