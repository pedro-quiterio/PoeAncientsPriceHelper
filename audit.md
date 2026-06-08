# Code Audit — Poe Ancients Price Helper

**Date:** 2026-06-08 · **Scope:** whole repo (`src/PoeAncientsPriceHelper` + `.Tests`), .NET 8 WPF/WinForms screen-overlay app.

Every item below was checked against the source and confirmed with a real `dotnet build` (output bundles `tessdata/eng.traineddata` + native `tesseract50.dll`/`leptonica` for x86 & x64) and `dotnet test` (**57 passed, 0 failed, 0 skipped**).

---

## How to read this document

There are two audiences, and the sections are ordered for both:

- **If you are the original author** → start at [§2 Action list](#2-action-list). It's a prioritized to-do you can work top-down; each row links to the detail. [§5 Verified non-issues](#5-verified-non-issues) tells you what you can stop worrying about.
- **If you are a new contributor** → start at [§1 Architecture map](#1-architecture-map) to learn the codebase, then skim [§4 Findings](#4-findings) for the sharp edges before you change anything. [§6 Strengths](#6-strengths) describes conventions worth preserving.

**Severity:** **P1** breaks/degrades the app for users · **P2** real correctness/robustness/perf risk · **P3** polish, maintainability, defense-in-depth. There are currently **no P1s** and **no security vulnerabilities** — the app reads the screen, calls two known HTTP hosts, and draws an overlay; no RCE surface, no credentials, no listening sockets.

---

## 1. Architecture map

Pipeline, in dependency order. All files are in [src/PoeAncientsPriceHelper/](src/PoeAncientsPriceHelper/).

| File | Role |
|------|------|
| [App.xaml.cs](src/PoeAncientsPriceHelper/App.xaml.cs) | Entry point. Single-instance mutex; global hotkeys via SharpHook (Esc / Ctrl+Click dismiss, F3 debug); `--ocr-test` headless mode; `--debug` console. |
| [MainWindow.xaml.cs](src/PoeAncientsPriceHelper/MainWindow.xaml.cs) | Settings window. Owns the singleton `HttpClient`, wires up repo/icons/engine, F4 recalibrate hotkey, Start/Stop. |
| [AppConfig.cs](src/PoeAncientsPriceHelper/AppConfig.cs) / [ConfigStore.cs](src/PoeAncientsPriceHelper/ConfigStore.cs) | Settings model + JSON load/save (`config.json` next to the exe). |
| [PriceRepository.cs](src/PoeAncientsPriceHelper/PriceRepository.cs) | Fetches prices from poe.ninja (4 exchange types), normalizes names → price dict, 30-min auto-refresh, `custom_prices.json` overrides. |
| [IconCache.cs](src/PoeAncientsPriceHelper/IconCache.cs) | Downloads currency icons from poecdn once, caches to disk. |
| [ScreenCapture.cs](src/PoeAncientsPriceHelper/ScreenCapture.cs) | `CopyFromScreen` of the calibrated region. |
| [ListDetector.cs](src/PoeAncientsPriceHelper/ListDetector.cs) | Brightness gate: is the in-game panel open? |
| [OcrScanner.cs](src/PoeAncientsPriceHelper/OcrScanner.cs) | Tesseract OCR. Crops icon column, inverts, upscales, runs two segmentation passes concurrently, merges by row position. |
| [ScanEngine.cs](src/PoeAncientsPriceHelper/ScanEngine.cs) | **The brain.** Capture→detect→OCR→price-match loop with hysteresis, per-row slot locking, fuzzy name matching, panel-switch detection. |
| [PriceOverlay.cs](src/PoeAncientsPriceHelper/PriceOverlay.cs) | Click-through WinForms overlay (on its own STA thread) that paints prices next to each row. |
| [CalibrationOverlay.cs](src/PoeAncientsPriceHelper/CalibrationOverlay.cs) | Full-screen drag-to-select region picker. |
| [ChangeDetector.cs](src/PoeAncientsPriceHelper/ChangeDetector.cs) | FNV-1a frame hashing — **currently dead code** (see [4.7](#47-changedetector-is-dead-code-p3)). |

**Threading model** (important to grasp before editing): the scan loop runs on a background `Task` ([ScanEngine.RunLoopAsync](src/PoeAncientsPriceHelper/ScanEngine.cs)); the price overlay runs `Application.Run` on a **separate dedicated STA thread** ([PriceOverlayManager.EnsureVisible](src/PoeAncientsPriceHelper/PriceOverlay.cs)); the settings UI is on the WPF dispatcher thread; the 30-min price refresh and SharpHook callbacks fire on thread-pool threads. Cross-thread coordination uses `BeginInvoke`/`Invoke` into the overlay and a couple of `static volatile` flags in `ScanEngine`.

---

## 2. Action list

Ordered by value/effort. Nothing here is urgent — the app works today.

| # | Do this | Sev | Effort | Detail |
|---|---------|-----|--------|--------|
| 1 | Configure the `HttpClient`: `Timeout`, and thread a `CancellationToken` through the fetch path | P2 | S | [4.1](#41-httpclient-has-no-timeout-or-cancellation-p2) |
| 2 | Make `ConfigStore.Save` atomic (temp file + `File.Replace`); make its path injectable and test the real class | P2 | S | [4.2](#42-configstoresave-is-not-crash-safe-p2) |
| 3 | Memoize fuzzy matches per OCR string (invalidate the cache on price refresh) | P2 | S | [4.3](#43-fuzzy-name-matching-is-the-loops-main-cpu-cost-p2) |
| 4 | Add tests for `MergeReads`/slot-locking and panel-switch/eviction logic | P3 | M | [4.4](#44-the-highest-value-untested-logic-is-the-slot-state-machine-p3) |
| 5 | Route non-scan errors to the file log / `StatusLabel` instead of `Console.Error` | P3 | S | [4.5](#45-most-error-logging-goes-to-a-console-that-doesnt-exist-p3) |
| 6 | Tighten overlay thread-safety: mark `_form` `volatile`/read under lock; close the construction-ordering window | P3 | S | [4.6](#46-priceoverlaymanager-has-thread-visibility-gaps-p3) |
| 7 | Remove `ChangeDetector` — or wire it into the loop to skip OCR on unchanged frames | P3 | S | [4.7](#47-changedetector-is-dead-code-p3) |
| 8 | Gate `debug_ocr.png` writes behind a `debug` flag passed into `OcrScanner` | P3 | S | [4.8](#48-debug_ocrpng-is-written-unconditionally-p3) |
| 9 | Multi-monitor: size calibration **and** overlay to `VirtualScreen`, not `PrimaryScreen` | P3 | M | [4.9](#49-multi-monitor-everything-assumes-the-primary-screen-p3) |
| 10 | Harden the easter-egg matching against false positives | P3 | S | [4.10](#410-easter-egg-matching-is-loose-p3) |
| 11 | Housekeeping: CI workflow, fix author metadata, drop dead `ReferencePixelColor` + stale comment, check hotkey-registration return values | P3 | S | [4.11](#411-housekeeping-p3) |

---

## 3. HttpClient configuration (requested deep-dive)

**Current state:** one `HttpClient` is created as a `MainWindow` field (`new()`), lives for the whole app, and is shared by `PriceRepository` and `IconCache`. It talks to exactly two hosts — `poe.ninja` (4 sequential GETs every 30 min) and `web.poecdn.com` (≤4 icon GETs once at startup, then cached to disk). PayPal opens in the browser, not via this client. So this is a **low-volume, low-concurrency, long-lived** client.

**Worth setting:**

| Setting | Why | Value |
|---------|-----|-------|
| `Timeout` | Default is **100 s**; a stalled connection blocks a whole fetch cycle that long. | `TimeSpan.FromSeconds(15)` |
| `DefaultRequestVersion = HttpVersion.Version20` | Both hosts support HTTP/2; lets the 4 sequential poe.ninja calls multiplex over one connection. Small latency win. | `HttpVersion.Version20` |
| A default `User-Agent` | `PriceRepository` sets a (spoofed) UA per request; `IconCache` sets none. A single default is cleaner and avoids per-host surprises. | — |

**Deliberately *not* set (and why — so nobody adds them later thinking they help):**

- **`MaxConnectionsPerServer`** — *the one originally asked about.* On modern .NET (`SocketsHttpHandler`) the default is already `int.MaxValue`; the old 2-connection `ServicePointManager` cap does **not** apply. This setting *caps* concurrency to avoid overwhelming a server — but this app issues ≤4 *sequential* requests per host every 30 min and will never open more than 1–2 connections anyway. Setting it would have **zero effect**.
- **`PooledConnectionLifetime`** — solves the "singleton client ignores DNS changes" problem for clients hitting IP-rotating load balancers. Technically applicable (the client is long-lived) but near-worthless here: DNS stability across one gaming session is a non-issue, and a stale connection just fails one fetch that retries in 30 min. Optional at most (`FromMinutes(15)`).
- **`MaxResponseContentBufferSize`** — a cap against huge/malicious responses. Endpoints are trusted and payloads are tiny (small JSON + small PNGs). Skip.
- **`IHttpClientFactory`** — the canonical "do HttpClient right" pattern, but it exists to prevent socket exhaustion from creating *many* clients in DI/ASP.NET apps. This app creates exactly one. Over-engineering here.

**Bottom line:** `Timeout` is the only setting that materially matters; HTTP/2 + a default UA are nice polish. Connection-pool/factory tuning is the right instinct *in general* but is aimed at high-concurrency scenarios this code doesn't have. This is tracked as [action #1](#2-action-list).

Suggested implementation:
```csharp
private static readonly SocketsHttpHandler _httpHandler = new()
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(15), // optional; harmless DNS refresh
};

private readonly HttpClient _http = new(_httpHandler)
{
    Timeout = TimeSpan.FromSeconds(15),
    DefaultRequestVersion = HttpVersion.Version20,
};
```

---

## 4. Findings

### 4.1 `HttpClient` has no timeout or cancellation (P2)
[MainWindow.xaml.cs:15](src/PoeAncientsPriceHelper/MainWindow.xaml.cs#L15) is `new HttpClient()` — default 100 s timeout. The 30-min refresh (`PriceRepository.StartAutoRefresh`) does `Task.Run(() => FetchAndMergeAsync(config))` with no token, and `FetchTypeAsync` awaits `SendAsync(req)` with no token. A hung connection stalls a refresh cycle and can't be cancelled on shutdown. See [§3](#3-httpclient-configuration-requested-deep-dive) for the full client config. **Fix:** set `Timeout`; thread a `CancellationToken` from the timer/shutdown through `FetchAndMergeAsync` → `FetchTypeAsync`.

### 4.2 `ConfigStore.Save` is not crash-safe (P2)
[ConfigStore.cs:21-25](src/PoeAncientsPriceHelper/ConfigStore.cs#L21-L25) does `File.WriteAllText` (truncate-then-write); a crash mid-write corrupts `config.json`, and `Load` silently falls back to defaults — **the user loses their calibration**. Probability is low (writes only happen on calibrate/league-change; the file is tiny), but the fix is cheap. **Fix:** write to a temp file and `File.Replace`. While here, make the path injectable so the *real* `ConfigStore` can be unit-tested (today it hard-codes `AppContext.BaseDirectory`, so [ConfigStoreTests](src/PoeAncientsPriceHelper.Tests/ConfigStoreTests.cs) reimplements the logic and the real class is untested).

### 4.3 Fuzzy name matching is the loop's main CPU cost (P2)
[`BestFuzzy`](src/PoeAncientsPriceHelper/ScanEngine.cs#L302-L314) scans all price keys (with a ±3 length pre-filter and early `continue`) running Levenshtein on the survivors, for each unresolved OCR row. **In context it's bounded:** exact and prefix hits skip it entirely (it's the last resort), and once a row **locks** the slot stops changing — so the cost concentrates in the first ~200–300 ms after a panel opens, not steady-state. Still the top CPU contributor and an easy win. **Fix:** memoize results in a `Dictionary<string,string?>` keyed by normalized OCR string. **Important:** invalidate that cache on every price refresh (the key set changes every 30 min) or it will serve stale matches.

### 4.4 The highest-value untested logic is the slot state machine (P3)
The pure string helpers are well-covered (`NormalizeName`, `StripLeadingNoise` 12 cases, `ExtractMultiplier` 9 cases, `Levenshtein`/fuzzy). What's **not** tested is the trickiest, most flakiness-prone code: [`MergeReads`](src/PoeAncientsPriceHelper/ScanEngine.cs#L349)/`RowSlot` locking, `MergeByPosition`, and the panel-switch/eviction path. These are pure and deterministic given a list of reads, so they're testable without a screen or OCR engine — just harder to set up than the string helpers. Worth doing because regressions here cause the subtle "wrong/frozen price" bugs.

### 4.5 Most error logging goes to a console that doesn't exist (P3)
`PriceRepository` (×4), `IconCache` (×1), and `MainWindow.DonateButton_Click` (×1) log failures via `Console.Error.WriteLine`, but the app is `WinExe` with **no console** unless launched with `--debug`. So the *reason* for a fetch/icon failure is invisible to normal users. (The *symptom* of a total fetch failure is partly visible — `UpdateStatusLabel` shows "0 items loaded".) `ScanEngine` already does this right with `scan_log.txt`. **Fix:** route these to the same file log and/or surface them in `StatusLabel`.

### 4.6 `PriceOverlayManager` has thread-visibility gaps (P3)
Two issues in [PriceOverlay.cs](src/PoeAncientsPriceHelper/PriceOverlay.cs):
1. `_form` is read by `UpdateState`/`ForceTopmost`/etc. **without** a lock or `Volatile.Read`, so they can briefly observe a stale reference. (Reference reads are atomic in .NET — this is a *visibility/ordering* gap, not a torn read. Every reader null-checks and the form guards on `IsDisposed`/`IsHandleCreated`, so worst case is a dropped frame.)
2. `_form` is assigned **inside** the overlay-thread lambda while `EnsureVisible` only waits on the `Shown` event — a small construction-ordering window where `UpdateState` could fire before the handle exists.

**Fix:** mark `_form` `volatile` (or read it under `_lock` everywhere), and set `_form` before signaling readiness.

### 4.7 `ChangeDetector` is dead code (P3)
[ChangeDetector.cs](src/PoeAncientsPriceHelper/ChangeDetector.cs) (FNV-1a frame hashing) has **zero callers** anywhere in `src`. It looks like an intended optimization (skip OCR when the captured frame is identical to the last) that was never wired up. **Fix:** either delete it, or call `HasChanged` in the scan loop before OCR to skip redundant passes — which would also reduce the [4.3](#43-fuzzy-name-matching-is-the-loops-main-cpu-cost-p2) cost.

### 4.8 `debug_ocr.png` is written unconditionally (P3)
[OcrScanner.cs:60-64](src/PoeAncientsPriceHelper/OcrScanner.cs#L60-L64) saves `debug_ocr.png` whenever a scan yields ≤2 rows — every ~100 ms during a panel that's mis-detected as open. It's wrapped in try/catch so it can't crash, but it's needless disk churn for end users. **Fix:** pass a `bool debug` into the `OcrScanner` ctor and gate the save on it. (`OcrScanner` is engine-level and has no reference to `App.DebugMode`, so don't reach for that static — inject the flag.)

### 4.9 Multi-monitor: everything assumes the primary screen (P3)
Both [CalibrationOverlay.cs:32](src/PoeAncientsPriceHelper/CalibrationOverlay.cs#L32) and [PriceOverlay.cs:269](src/PoeAncientsPriceHelper/PriceOverlay.cs#L269) use `Screen.PrimaryScreen!.Bounds`. The sharper limitation is at **calibration**: the picker is sized to the primary screen, so a user can't even draw a region on a second monitor. (Most players run the game on the primary monitor, so the real-world hit is narrower than it sounds.) **Fix:** size both the calibration form and the overlay to `SystemInformation.VirtualScreen` so they span all monitors, and offset coordinates accordingly.

### 4.10 Easter-egg matching is loose (P3)
[ScanEngine.cs:249-258](src/PoeAncientsPriceHelper/ScanEngine.cs#L249-L258) maps any OCR row containing `"random"`+`"currency"` → Mirror and `"unique"`+`"belt"` → Headhunter, and constructs them with `ExactMatch: true` so they **lock on the first read**. This is intended fun, not a bug, but a real item that OCRs into those word pairs would be permanently mispriced as a meme. **Fix (only if it ever false-positives):** match exact normalized phrases, or put it behind a flag.

### 4.11 Housekeeping (P3)
- **No CI / analyzers / `.editorconfig`.** Tests exist but nothing runs them automatically. The build is already 0-warning with `Nullable`+`ImplicitUsings` enabled, so this is future-proofing: add a `dotnet test` GitHub Action.
- **Author metadata** in [csproj:16-18](src/PoeAncientsPriceHelper/PoeAncientsPriceHelper.csproj#L16-L18) (`pedro`) doesn't match the git committer or the donate email. Cosmetic; align before any public release.
- **Dead config field:** `AppConfig.ReferencePixelColor` is marked unused but still serialized, and [CalibrationOverlay.cs:7-8](src/PoeAncientsPriceHelper/CalibrationOverlay.cs#L7-L8) has a stale comment promising a "reference pixel color sampled at its centre" that no longer happens. Remove both.
- **Unchecked P/Invokes:** `RegisterHotKey`/`UnregisterHotKey` ([MainWindow.xaml.cs:32](src/PoeAncientsPriceHelper/MainWindow.xaml.cs#L32),[:156](src/PoeAncientsPriceHelper/MainWindow.xaml.cs#L156)) ignore their `bool` return. If another app owns F4, the hotkey silently dies (the button still works). A debug-log on failure would aid support.

---

## 5. Verified non-issues

Checked and found **not** to be problems — listed so they don't get re-flagged:

- **OCR data / native libs are bundled automatically.** The `Tesseract.Data.English` package's `build/*.targets` copies `tessdata/eng.traineddata` into the output, and `Tesseract` stages `x86`/`x64` `tesseract50.dll` + `leptonica`. A plain `dotnet build`/`publish` produces a working OCR app with no manual steps. (The `Directory.Exists(tessdata)` guard in `ScanEngine` is just belt-and-suspenders.) *Caveat:* this is implicit — if someone removes the `Tesseract.Data.English` PackageReference, OCR silently breaks at runtime. A one-line README note would prevent that.
- **Custom-price file deserialization is safe.** [`ApplyCustomOverride`](src/PoeAncientsPriceHelper/PriceRepository.cs#L132-L154) uses a typed generic deserialize with no `TypeNameHandling`, wrapped in try/catch. Newtonsoft 13.0.3 is current. Local, user-owned data — no gadget risk.
- **`_lastPositions`** is `private` and only touched inside the single-threaded scan loop. Safe.
- **`obj/`/`bin/` are not tracked** despite existing locally — `.gitignore` covers them; `git ls-files` shows 29 source files only.
- **`MaxConnectionsPerServer` does not need setting** — see [§3](#3-httpclient-configuration-requested-deep-dive).

---

## 6. Strengths (conventions to preserve)

If you're contributing, match these — they're why the codebase is easy to work in:

- **Comments explain *why*, and often *which bug* motivated the code** (the single-instance mutex story, brightness hysteresis, two-engine concurrency, the dismiss-latch). Keep writing comments like these.
- **Disciplined disposal.** `IDisposable` is implemented where it matters and [`MainWindow.Window_Closing`](src/PoeAncientsPriceHelper/MainWindow.xaml.cs#L154) tears down engine→repo→icons→http in order.
- **Defensive loop.** Every I/O and the whole scan cycle is wrapped so one bad frame never crashes the app.
- **Tests fake the network** (`FakeHttpMessageHandler`) and target the genuinely tricky pure logic. Add to this rather than reaching for integration tests.
- **Click-through overlay done correctly** via `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE` ([PriceOverlay.cs:49-60](src/PoeAncientsPriceHelper/PriceOverlay.cs#L49-L60)).
- **Sensible `.gitignore`** excludes build output, generated runtime files, downloaded icons, and local tooling.
