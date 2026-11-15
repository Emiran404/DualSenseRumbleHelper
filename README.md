# DualSense Rumble Helper

Get PS5 **DualSense rumble working in Rocket League on PC**. A single, portable Windows exe: no installer, no admin rights for normal use.

[Türkçe özet aşağıda](#türkçe)

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-blue) ![License](https://img.shields.io/badge/license-MIT-green)

## Download

Grab `DualSenseRumbleHelper.exe` from [Releases](https://github.com/Emiran404/DualSenseRumbleHelper/releases) and run it. It is a self-contained .NET 8 app (about 66 MB), so nothing else needs to be installed for the app itself.

Settings live in `%LOCALAPPDATA%\DualSenseRumbleHelper\settings.json` (a `crash.log` lands there too if something goes wrong).

## Three modes

| Mode | How it works | Needs |
| --- | --- | --- |
| **1 · Steam Input** (no drivers) | Finds Rocket League (Epic or Steam), walks you through adding it to Steam as a non-Steam game and turning on PlayStation support. Steam then does the rumble. | Steam |
| **2 · ViGEm bridge** (recommended) | Reads the DualSense over HID (USB or Bluetooth), feeds a virtual Xbox 360 controller, and writes the game's rumble back to the DualSense. Optional HidHide integration hides the real pad to avoid double input. Motors are zeroed when it stops. | [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases) (one-time install) |
| **3 · XInput proxy DLL** (advanced) | Puts a small XInput DLL (written in C) next to `RocketLeague.exe`, backing up the original; Uninstall restores it. **Rocket League uses Easy Anti-Cheat; this mode may break the game or risk your account.** Never the default, and it requires explicit consent in the app. | Nothing |

## Features

- Windows 11 (Fluent / Mica) interface, light, dark or system theme
- **Languages:** English, Türkçe, Deutsch, Español, Français, Português (Brasil); switches live
- Tray icon, start with Windows, start minimized
- Auto-start the bridge when Rocket League launches, auto-stop when it closes
- Motor tuning: left/right strength (0-150 %), threshold, swap, rumble test
- Lightbar modes: static, breathing, rumble-reactive, battery level, rainbow; colour palette with Rocket League team colours
- Conflict detection (DS4Windows, DSX, reWASD, Steam Input, extra virtual pads, HidHide)
- Single instance; a second launch brings the window to front

## Build from source

Requirements: .NET 8 SDK. The native proxy DLL is built with [Zig](https://ziglang.org) (`zig cc`); the build script downloads it if missing.

```powershell
.\build.ps1        # proxy DLL + icon + single-file exe into .\dist
```

Handy flags while developing:

```powershell
DualSenseRumbleHelper.exe --selftest out.txt   # HID, proxy DLL and ViGEm end-to-end check
DualSenseRumbleHelper.exe --multi --page 4     # second instance, open a given page (1-5)
DualSenseRumbleHelper.exe --lang de            # override the UI language for this run
```

## Translating

Strings live in `DualSenseRumbleHelper/Resources/Lang/<code>.json` (flat `"key": "text"`). To add a language, copy `en.json`, translate the values (keep `{0}`-style placeholders), and add the code to `Loc.Languages` in `Services/Loc.cs`. Missing keys fall back to English.

## Status

Tested: Mode 2 in a real Rocket League match (DualSense over Bluetooth), HID input/output reports, ViGEm rumble feedback, proxy DLL exports and fallback.
Not yet tested end to end: Mode 3 together with EAC, the full HidHide flow, lightbar modes on every controller firmware.

## Credits

- [WPF UI](https://github.com/lepoco/wpfui) (MIT): Fluent interface
- [ViGEmBus](https://github.com/nefarius/ViGEmBus) and [HidHide](https://github.com/nefarius/HidHide) by Nefarius Software Solutions
- [.NET](https://github.com/dotnet/runtime) (MIT), [Zig](https://ziglang.org) (MIT)

## License

[MIT](LICENSE) © 2026 Emiran404

DualSense and PlayStation are trademarks of Sony Interactive Entertainment. Rocket League is a trademark of Psyonix. Xbox is a trademark of Microsoft. This is an independent, unofficial project, not affiliated with or endorsed by these companies.

---

## Türkçe

Rocket League (PC) için PS5 DualSense titreşimini çalıştıran, kurulum gerektirmeyen tek dosya Windows uygulaması.

- **Mod 1 · Steam Input:** Sürücü gerekmez; Steam'e "Steam dışı oyun" ekleme adımlarını gösterir.
- **Mod 2 · ViGEm köprüsü (önerilen):** DualSense'i USB/Bluetooth ile okur, sanal Xbox 360 koluna aktarır ve oyunun titreşimini kola geri yazar. ViGEmBus bir kez kurulmalı.
- **Mod 3 · Proxy DLL (ileri düzey):** Oyun klasörüne XInput DLL'i koyar. Easy Anti-Cheat riski vardır; varsayılan değildir ve açık onay ister.

Arayüz Türkçe, İngilizce, Almanca, İspanyolca, Fransızca ve Portekizce (Brezilya) dillerini destekler. Dil, Ayarlar → Dil bölümünden anında değiştirilebilir.
