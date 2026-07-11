# Poe Ancients Price Helper

A lightweight screen overlay for **Path of Exile 2**. It watches a calibrated region of your screen,
reads the currency / reward list with OCR, looks up live prices from [poe.ninja](https://poe.ninja/poe2),
and draws a click-through price overlay next to each item — so you never have to alt-tab to check what a
stack is worth.

## Features

- **Live prices** next to each list row, sourced from poe.ninja (auto-refreshed every 30 minutes).
- **Stack-aware** — shows the total and the per-item price, e.g. `2 (0.5 each)`.
- **Uncut gems** (skill / spirit / support) priced by exact type **and level** — a row shows `?`
  rather than a guessed price if the gem type or level can't be read cleanly (neighbouring levels
  can differ several-fold, so a wrong-level price would be misleading).
- **GPU-accelerated capture** — uses Windows Graphics Capture (WGC) by default for low CPU usage,
  with automatic fallback to legacy GDI if WGC is unavailable.
- **Windows OCR engine** — uses the native `Windows.Media.Ocr` (WinRT) for fast, accurate detection
  of on-screen text. No external OCR dependencies.
- **Automatic updates (since v3.0.0)** — installs and updates itself from GitHub Releases. When a new
  version is out, click **Update now** in the app (or just close it and the update is applied silently
  the next time you launch). No re-downloading, no re-unzipping, and **your calibration and settings are
  kept**. See [How updates work](#how-updates-work).
- **Click-through overlay** that never gets in the way of the game.
- **One-time calibration** — just drag a box around the in-game list panel.
- **Hotkeys:** `F5` start/stop · `F4` recalibrate · `F3` debug boxes · `Esc` / `Ctrl+Click` hide.
- **Minimize to tray** — scanning keeps running in the background.
- **🎨 Theme switcher** — 5 dark themes (Toxic, Midnight, Obsidian, Abyss, Ember). Defaults to
  **Toxic** — its dark green gradient complements the green Start button while keeping the same
  low-light feel.
- **🗺️ Island Rumour helper (experimental)** — optionally watches the Atlas and shows each rumour's
  map / mods / rating (from a community spreadsheet) next to the *Uncharted Waters / Island Rumours*
  panel. It finds the Atlas by the **WORLD** label automatically; on windowed / custom-resolution
  setups where that can't be read, turn off auto-detect in **Settings** and drag a box over the WORLD
  label yourself.
- **Pauses when you tab away** — scanning stops while the game isn't the active window and resumes
  when you return, so it isn't wasting cycles in the background. If it ever misbehaves on an unusual
  focus setup, you can turn this off in **Settings → “Only scan while Path of Exile is the active
  window”** to keep pricing running regardless of what's in front.

## Download & install

Grab **`PoeAncientsPriceHelper-win-Setup.exe`** from the [**Releases**](../../releases) page and run it.
It installs per-user (no admin required) and launches the app. No .NET runtime needed — it's a
self-contained Windows x64 build.

That's the **only** time you'll download it by hand: from then on the app keeps itself up to date and
remembers your calibration and settings (see [How updates work](#how-updates-work)).

> Windows SmartScreen may warn that the app is unsigned — click **More info → Run anyway**.

> **Upgrading from the old zip (v2.x or earlier)?** This is the last manual step. Run the new
> `Setup.exe` once; because the app now stores settings in a stable location, you'll re-pick your
> league and re-calibrate **one final time** — after that, updates are automatic and your settings
> persist across them.

## How updates work

The app updates itself straight from this repo's GitHub Releases — there's no separate update server.

- On startup it quietly checks for a newer release and, if there is one, downloads it in the background.
- When ready, an **Update now** link appears in the settings window — click it to install and relaunch
  instantly into the new version.
- Prefer not to interrupt what you're doing? Just ignore it. The next time you **close** the app, the
  staged update is applied silently, so you're already on the new version the next time you open it.
- Your `config.json` (calibration, league, hotkeys, theme) lives in `%LocalAppData%\PoeAncientsPriceHelper`
  and is **kept across updates** — updating never resets your settings.

Updating is powered by [Velopack](https://velopack.io/) (see [Acknowledgements](#acknowledgements)).

## Troubleshooting

### Prices won't load / poe.ninja fetches keep failing

On some connections (Starlink and other CGNAT setups are the usual culprits) the IPv6 network path
is broken even though IPv4 works fine. Since `poe.ninja` resolves to both IPv6 and IPv4 addresses,
the app can end up trying the dead IPv6 route and stalling until each request times out — so prices
never appear.

**As of v3.5.7 this is handled automatically** — the app races the IPv4 and IPv6 routes and uses
whichever connects first, so a dead IPv6 path falls back to IPv4 on its own. Just update and it
should work; no firewall rule needed.

If you're on an older version and can't update, you can force the app onto IPv4 by blocking its
outbound IPv6 traffic with a firewall rule. Open **Windows PowerShell as Administrator** and run:

```powershell
New-NetFirewallRule -DisplayName "Block IPv6 - PoeAncientsPriceHelper" `
  -Direction Outbound `
  -Program "C:\Users\<YOUR-USERNAME>\AppData\Local\PoeAncientsPriceHelper\current\PoeAncientsPriceHelper.exe" `
  -RemoteAddress "::-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff" `
  -Protocol TCP `
  -Action Block
```

Replace `<YOUR-USERNAME>` with your Windows username (the path above is the default install
location). Restart the app and prices should load over IPv4.

To undo it later:

```powershell
Remove-NetFirewallRule -DisplayName "Block IPv6 - PoeAncientsPriceHelper"
```

> Thanks to the community for diagnosing this one.

### The overlay stays hidden / prices never appear while I'm playing

The overlay pauses itself while Path of Exile isn't the active window (so it isn't scanning in the
background). On most setups it resumes the moment you're back in the game, but some unusual focus
configurations can make it read the game as “not in front” even while you're playing — the overlay
then stays hidden. If that happens, open **Settings** and untick **“Only scan while Path of Exile is
the active window.”** Pricing will then run regardless of what's focused. (The **Open logs** link on
the main window shows a `paused (game not foreground)` line with the window that grabbed focus, if
you want to report the setup.)

### Some antivirus software flags it as malware

Some antivirus engines may flag the download with reputation- or ML-based verdicts (names like
`FileRepMalware` or a `*.ml.score` "moderate" score). **These are false positives**, and here's
exactly why they happen:

- The app is **unsigned** (no code-signing certificate — those cost money a free tool doesn't have).
- It's a **new, self-contained build** that few people have downloaded yet, so it has no established
  file reputation. Verdicts like these are *prevalence*-based — "this file is rare and unsigned" —
  **not** a match against any known malware.
- It does exactly the things heuristics are trained to be suspicious of: **captures the screen**,
  registers **global hotkeys**, and makes **network requests** — all of which are core, documented
  features of a price overlay.

Your reassurance is that **the full source is right here in this repo** — you can read every line,
and you can build it yourself (see [Build from source](#build-from-source)) instead of trusting the
prebuilt download. The vast majority of engines return clean; only a handful of reputation/ML
heuristics trip on a new, unsigned binary.

If you'd like, you can help by reporting the false positive to your antivirus vendor — most have a
"submit a false positive" form. These verdicts clear on their own as the release ages and more
people download it.

## Build from source

Requires the **.NET 10 SDK** ([download](https://dotnet.microsoft.com/en-us/download/dotnet/10.0))
and **Windows 10 version 2004+** / Windows 11.

```sh
# restore + build
dotnet build src/

# run tests
dotnet test src/PoeAncientsPriceHelper.Tests/

# build a self-contained release
dotnet publish src/PoeAncientsPriceHelper/ -c Release -r win-x64 --self-contained true -o publish
```

## Capture backend

The screen capture method is configurable via `config.json`:

| Value | Description |
|---|---|
| `"Auto"` (default) | Uses WGC (GPU-based) with automatic GDI fallback per frame |
| `"GDI"` | Forces legacy BitBlt capture (higher CPU, universal compatibility) |

WGC requires Windows 10 2004+. If WGC fails at runtime, the app silently falls back to GDI without
crashing.

## Tech

- **.NET 10** (`net10.0-windows10.0.19041.0`) — WPF (settings window) + WinForms (overlay)
- **Windows.Media.Ocr** (WinRT) for OCR — no external dependencies
- **Windows Graphics Capture** via Vortice.Direct3D11 + WinRT interop for screen capture
- **poe.ninja** API for live price data (parallel fetch over HTTP/2, 30-min auto-refresh)
- **SharpHook** for global hotkeys
- **WPF UI** (lepoco) for the settings window UI
- **Velopack** for the installer and automatic updates

## Acknowledgements

This app builds on these open-source projects:

- **[Velopack](https://github.com/velopack/velopack)** — installer & auto-update framework, © Caelan
  Sayler / Velopack Ltd., [MIT License](https://github.com/velopack/velopack/blob/develop/LICENSE).
- **[WPF UI](https://github.com/lepoco/wpfui)** (lepoco) — MIT License.
- **[SharpHook](https://github.com/TolikPylypchuk/SharpHook)** — MIT License.
- **[Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)** — MIT License.
- **[Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json)** — MIT License.
- Price data from **[poe.ninja](https://poe.ninja/poe2)** (unofficial API).

## Support

If this tool saves you some alt-tabbing, there's a **☕ Buy me a coffee** button right in the app.
Thanks!

## Disclaimer for those who seem to be troubled by it.. 
Yes it was greatly helped by AI :D never the less it works and its free!
