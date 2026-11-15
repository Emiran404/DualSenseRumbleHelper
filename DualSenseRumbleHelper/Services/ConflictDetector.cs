using System.Diagnostics;
using DualSenseRumbleHelper.Native;

namespace DualSenseRumbleHelper.Services;

public sealed record Conflict(string Title, string Detail, string Kind = "");

/// <summary>Detects other controller bridges / virtual pads that would fight with the chosen mode.</summary>
public static class ConflictDetector
{
    private static readonly string[] BridgeProcesses =
    {
        "DS4Windows", "DualSenseX", "DSX", "reWASD", "reWASDEngine", "reWASDService", "JoyShockMapper",
        "x360ce", "x360ce_x64", "InputMapper", "XOutput", "XOutputApp", "BetterJoy", "SCController", "AntiMicroX", "antimicrox",
    };

    public static List<Conflict> Detect()
    {
        var list = new List<Conflict>();

        // 1) Known bridge applications
        foreach (var name in BridgeProcesses)
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0)
                    list.Add(new Conflict(F("conflict.appRunning", name), T("conflict.appRunningDetail"), "app"));
            }
            catch { }
        }

        // 2) Virtual Xbox 360 pads (ViGEmBus) and Steam's virtual pad
        try
        {
            int vigemPads = 0, steamPads = 0, realX360 = 0;
            foreach (var id in Win32.GetPresentDeviceIds())
            {
                if (id.Contains("VID_045E&PID_028E", StringComparison.OrdinalIgnoreCase) && id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                {
                    string? parent = Win32.GetParentDeviceId(id);
                    if (parent != null && parent.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)) vigemPads++;
                    else realX360++;
                }
                else if (id.Contains("VID_28DE&PID_11FF", StringComparison.OrdinalIgnoreCase))
                {
                    steamPads++;
                }
            }
            if (vigemPads > 0)
                list.Add(new Conflict(F("conflict.vigemPads", vigemPads), T("conflict.vigemPadsDetail"), "vigem"));
            if (steamPads > 0)
                list.Add(new Conflict(T("conflict.steamPad"), T("conflict.steamPadDetail"), "steam"));
        }
        catch { }

        // 3) HidHide cloak hides the real DualSense from Steam/game
        try
        {
            if (HidHideService.IsInstalled && HidHideService.GetCloakState() == true)
                list.Add(new Conflict(T("conflict.hidhide"), T("conflict.hidhideDetail"), "hidhide"));
        }
        catch { }

        return list;
    }
}
