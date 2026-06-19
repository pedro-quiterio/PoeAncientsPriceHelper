<div align="center">

# ⚡ Poe Ancients Price Helper

A lightweight, click-through screen overlay for **Path of Exile 2** that reads the
currency-exchange panel with OCR and shows live [poe.ninja](https://poe.ninja/poe2) prices
next to each item — so you never have to alt-tab mid-trade.

</div>

---

## 📌 About this fork

This is a fork of [pedro-quiterio/PoeAncientsPriceHelper](https://github.com/pedro-quiterio/PoeAncientsPriceHelper)
with a **ground-up performance, stability, and OCR overhaul**. A [PR](https://github.com/pedro-quiterio/PoeAncientsPriceHelper/pull/18)
has been opened upstream.

### What changed in v2.0.0

| Area | Before | After |
|---|---|---|
| **OCR engine** | Tesseract (document scanner) | **Windows.Media.Ocr** (native, GPU-accelerated) |
| **Detection** | 3 of 5 items, some never | **All 5 instantly** |
| **Screen capture** | GDI `CopyFromScreen` (CPU) | **WGC** (GPU via D3D11) + GDI fallback |
| **CPU usage** | High — full pipeline every cycle | **Low** — render skip, cached buffers, throttled intervals |
| **Price fetch** | Sequential HTTP | **Parallel HTTP/2** over single connection |
| **Stability** | Race conditions, GDI leaks | **Fixed** — atomic snapshots, concurrency hardening |

---

## ⚠️ Requirements (different from original)

> **These differ from the upstream project — read before installing.**

- **.NET 10 SDK** required to build from source (`dotnet build`)
  - Only the **Desktop Runtime** is needed if you just run the pre-built release
- **Windows 10 version 2004+** or Windows 11 (required for WGC + Windows OCR)
- **No Tesseract, no traineddata** — Windows OCR is built into Windows
- Self-contained build: **no .NET installation needed** if using the release zip

---

## ✨ Features

### 📊 Live price overlay
- **Real-time prices** next to each list row, sourced from poe.ninja
- **Auto-refreshed** every 30 minutes in the background
- **Stack-aware** — shows both the total and per-item price, e.g. `2 (0.5 each)`
- **Uncut gems** priced by exact type **and level** — shows `?` instead of a wrong-level guess

### 🔍 Windows OCR engine
- **Windows.Media.Ocr** — the native WinRT engine designed for on-screen text
- Dramatically faster and more accurate than document-oriented engines for game UI
- **3× upscaling** for glyph accuracy on small fonts
- No external dependencies or language model files

### 🖼️ GPU-accelerated screen capture
- **Windows Graphics Capture (WGC)** by default — runs on the GPU via D3D11, minimal CPU
- **Automatic GDI fallback** per-frame if WGC is unavailable or fails at runtime
- Configurable via `CaptureBackend` in `config.json` (`"Auto"` / `"GDI"`)

### 🎯 Smart matching pipeline
- **Exact → prefix → fuzzy** resolution chain with Levenshtein distance
- **Resolution cache** — OCR'd names resolved to price keys once, then cached
- **Length-bucketed fuzzy index** — only scans keys within ±3 characters
- **High-confidence fuzzy lock** — matches ≥0.92 similarity lock in 1 read

### 🖥️ Overlay quality
- **Click-through** — never gets in the way of the game
- **Per-pixel alpha** via `UpdateLayeredWindow` for genuine semi-transparency
- **Render skip** — only repaints when rows or state actually change
- **Cached render buffer** — avoids monitor-sized bitmap allocation per frame
- **Stale-clearing** — automatically hides prices during loading screens

### ⚡ Performance
- **OCR interval:** 150ms while panel open (~6.7 reads/s) — sub-200ms price turnaround
- **Idle polling:** 300ms while panel closed (~3.3 captures/s) — minimal idle CPU
- **Price fetch:** 5 categories in parallel over a single HTTP/2 connection
- **Matching:** resolution cache + length-bucketed fuzzy index avoid repeated Levenshtein work

### 🛡️ Stability
- **Atomic price snapshot** — prevents torn reads during background refresh
- **GDI handle leak** fixed in overlay rendering
- **Race condition fixes** — overlay concurrency, league-switch lifecycle, static flag reset
- **WGC resource management** — COM reference leaks fixed, frame pool recreated on resolution change

### ⌨️ Hotkeys
| Key | Action |
|---|---|
| `F5` | Start / stop scanning |
| `F4` | Recalibrate region |
| `F3` | Toggle debug overlay (row boxes, region outline, `?` text) |
| `Esc` | Hide overlay (resumes when panel reopens) |
| `Ctrl+Click` | Hide overlay (stays hidden until panel closes) |

### 🔧 Other
- **One-time calibration** — just drag a box around the in-game list panel
- **Minimize to tray** — scanning keeps running in the background
- **Per-Monitor-V2 DPI** awareness for mixed-DPI multi-monitor setups

---

## 🚀 Download & run

Grab the latest release from the [**Releases**](../../releases) page, unzip it anywhere,
and double-click **`Start.cmd`**.

> No install and no .NET runtime required — it's a self-contained Windows x64 build.
>
> Windows SmartScreen may warn that the app is unsigned — click **More info → Run anyway**.

---

## 🔨 Build from source

Requires the **.NET 10 SDK** and **Windows 10 version 2004+** / Windows 11.

```sh
# restore + build
dotnet build src/

# run tests
dotnet test src/PoeAncientsPriceHelper.Tests/

# build a self-contained release
dotnet publish src/PoeAncientsPriceHelper/ -c Release -r win-x64 --self-contained true -o publish
```

---

## ⚙️ Configuration

All settings live in `config.json` (created on first close):

| Setting | Default | Description |
|---|---|---|
| `CaptureBackend` | `"Auto"` | Screen capture: `"Auto"` (WGC + GDI fallback) or `"GDI"` |
| `RegionRect` | — | Calibrated screen region (set via `F4`) |
| `OverlayXOffset` | `0` | Horizontal offset of the price overlay |
| `LeagueName` | — | Active poe.ninja league |
| `CustomPricesPath` | `null` | Optional JSON file with price overrides |

---

## 🧱 Tech stack

| Component | Technology |
|---|---|
| Framework | **.NET 10** (`net10.0-windows10.0.19041.0`) — WPF (settings) + WinForms (overlay) |
| OCR | **Windows.Media.Ocr** (WinRT, GPU-accelerated) |
| Screen capture | **Windows Graphics Capture** via Vortice.Direct3D11 + WinRT interop |
| Price data | **poe.ninja** API (HTTP/2, parallel fetch, 30-min auto-refresh) |
| Hotkeys | **SharpHook** (global keyboard hook) |
| UI framework | **MahApps.Metro** |

---

## 📝 Changelog

See [**CHANGELOG.md**](CHANGELOG.md) for the full version history.

---

## 🔗 Links

- **Upstream repo:** [pedro-quiterio/PoeAncientsPriceHelper](https://github.com/pedro-quiterio/PoeAncientsPriceHelper)
- **PR:** [#18 — Full Performance & Stability Overhaul](https://github.com/pedro-quiterio/PoeAncientsPriceHelper/pull/18)
- **Release:** [v2.0.0 download](../../releases/tag/v2.0.0)

---

## 🤝 Support

If this tool saves you some alt-tabbing, there's a **Buy me a coffee** button right in the app.
Thanks!

---

## 📝 Disclaimer

Yes it was greatly helped by AI :D nevertheless it works and it's free!
