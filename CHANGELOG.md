# Changelog

All notable changes to **Poe Ancients Price Helper** are documented here.
The format is loosely based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [3.8.0] — 2026-09-05

### Added

- **Forbidden Rites league support.** The event league that launched with patch 0.5.5 and its Hardcore
  counterpart are now selectable in the League dropdown, alongside Runes of Aldur. Prices for both are
  live on poe.ninja across all five exchange categories the tool reads. Runes of Aldur stays the
  default, and your saved league choice is untouched.

## [3.7.1] — 2026-07-19

### Fixed

- **Mouse cursor no longer stutters while the tool runs.** On high-refresh / high-polling-rate setups
  (e.g. a 360 Hz monitor with a 1000 Hz mouse) the global input hook routed every mouse-move through the
  app and allocated per event, triggering frequent GC pauses that hitched the system cursor once or
  twice a second — and the hitching continued after "Stop" until the app was fully closed. The tool no
  longer installs a global mouse hook at all: the hook is keyboard-only, and the Left-Ctrl+click "dismiss
  overlay" gesture is now detected by a lightweight check that runs only while an overlay is on screen.
  Hotkeys, rebinding, Esc-to-dismiss and the Ctrl+click gesture are unchanged. (#52)
- **A hotkey pressed twice in quick succession no longer fires twice.** Some keyboards and overlay
  utilities emit a key press more than once; on Start/Stop that read as an instant stop-then-restart. A
  repeat of the same hotkey within a fraction of a second is now ignored. (#52)
- **The overlay can no longer get stuck hidden after a Ctrl+click dismiss.** Ctrl+click leaves the
  in-game panel open, and if the tool couldn't cleanly detect that panel later closing, the overlay
  stayed hidden until you toggled Start/Stop. It now always reappears once the panel is gone. (#52)

## [3.7.0] — 2026-07-11

### Added

- **New setting: “Only scan while Path of Exile is the active window.”** On by default (the existing
  behaviour — pricing pauses when you tab away and resumes when you return). Turn it off to keep pricing
  running no matter what's in front, for the rare focus setups where the game is wrongly read as
  not-in-front and the overlay stays hidden. (#49)

### Fixed

- **Overlay no longer pauses when you click the app's own windows.** Interacting with the Settings
  window, the debug console, or the click-through overlay counted as "tabbed away from the game" and
  paused pricing — sometimes for good. Focus held by the helper itself now keeps scanning. A brief
  focus blip no longer pauses instantly either: the game has to stay out of focus for about a second
  first, so momentary activation changes can't stop the overlay. The pause is also logged with the
  window that took focus, to make future reports diagnosable. (#49)

## [3.6.1] — 2026-07-10

### Fixed

- **Localized item rows that show a bare stack count now match their price.** On some panels the game
  prints the stack size as a plain `x1` after the item name (with no brackets), and OCR reads the `1`
  as a look-alike `l` — e.g. `Runa de erosión de Saqawal xl`. That stray `xl` corrupted the name and
  blocked the Spanish→English lookup, so the affected rows (such as the Saqawal runes) never priced.
  The stray marker is now stripped alongside the existing bracketed `(3)` form. Note that skill/support
  gems on the rune-combination panel are still not priced — they aren't tradeable currency. (#48)

## [3.6.0] — 2026-07-08

### Added

- **Hotkeys can now use modifier chords** such as `Ctrl+Q`, `Ctrl+Shift+Q`, or `Alt+F5`. Rebinding
  captures the modifiers you hold together with the final key, and the Settings window shows the chord
  (e.g. `Ctrl+Q`). Chords are matched exactly, so `Q` and `Ctrl+Q` are independent bindings that never
  trigger each other — a modifier chord is far less likely to fire by accident mid-combat. Existing
  single-key bindings and older config files keep working unchanged. (#46)

### Fixed

- **Overlay no longer stops working while the game is running.** The focus check added in 3.5.8 paused
  detection whenever Path of Exile wasn't the foreground window, but it demanded an exact match against
  the process's main window handle — which several client setups never satisfy, so the overlay paused
  and never resumed even with the game clearly in front. It now treats the game as focused whenever the
  foreground window belongs to the same process, and keeps scanning if it can't tell. (#47)

## [3.5.13] — 2026-07-07

### Fixed

- **Rumour ratings no longer change under you while a panel is open.** Each scan re-read and re-matched
  the panel from scratch, so a row could still flip between its rating and "unknown rumour" (or briefly
  jump to a different rumour) whenever the OCR read a line slightly differently — even after the 3.5.12
  de-flicker. The helper now remembers the best result it has resolved for each row and holds it steady:
  once a row is recognised it stays put, and only changes if a genuinely different rumour is read on two
  consecutive scans (i.e. the panel actually changed). It resets when the panel closes, so reopening a
  different panel still starts fresh. (#45)

## [3.5.12] — 2026-07-07

### Fixed

- **Rumour helper now detects the Atlas at custom / windowed resolutions.** The previous release
  magnified the on-screen "WORLD" label by the wrong amount at smaller resolutions — a wider detection
  band was enlarged *less*, so it read gibberish and the overlay never appeared (while default
  resolution and the price overlay kept working). Magnification is now a flat, sufficient amount, so
  "WORLD" is read reliably. Confirmed against a real failing capture. (#45)
- **Rumour overlay no longer flickers every 1-2 seconds.** It re-rendered on every scan even when
  nothing had changed, and wobbled as the detected panel bounds jittered a few pixels — making the
  ratings hard to read. It now repaints only when the displayed rumours or position actually change,
  and ignores sub-pixel-jitter movement, so it stays steady while a panel is open. (#45)

## [3.5.11] — 2026-07-06

### Fixed

- **Rumour helper now detects the Atlas at low / windowed resolutions.** The check that decides you're
  on the Atlas reads the on-screen **WORLD** label — but at smaller windowed / custom resolutions that
  stylised banner is only ~20px tall, which the OCR engine returned *nothing* for, so the rumour overlay
  never appeared (with auto-detect *or* a hand-set WORLD region). The gate region is now upscaled before
  reading, and the match tolerates a stylised-font misread, so it reads "WORLD" reliably. Confirmed
  against a real failing capture. (#45)

## [3.5.10] — 2026-07-06

### Fixed

- **Settings window no longer cuts off the bottom options.** The new "Auto-detect the Atlas map" /
  "Set WORLD region…" controls (3.5.9) fell below the fixed-height Settings window, so they were
  invisible and unreachable. The window is now taller and its contents scroll if they don't fit, so
  every option is always accessible. (#45)

## [3.5.9] — 2026-07-06

### Fixed

- **Rumour scanning now finds the game in windowed / custom-resolution setups.** The window it watches
  is now located by the Path of Exile **process** rather than only by matching the window title, which
  is more reliable across window decorations and layouts. (Follow-up to #45.)

### Added

- **Manual Atlas region for the rumour helper.** The helper decides it's on the Atlas by reading the
  **WORLD** label at the top of the map. On some windowed / custom-resolution setups that stylised
  label can't be read where it's expected, so the rumour overlay never appears. Settings now has an
  **"Auto-detect the Atlas map"** toggle (on by default); turn it off and click **"Set WORLD region…"**
  to drag a box over the WORLD label yourself. The guide (README) shows exactly what to select. (#45)

## [3.5.8] — 2026-07-05

### Fixed

- **Rumour scanning now works in windowed mode.** The "are we on the Atlas?" pre-check looked for the
  **WORLD** label in the top-centre strip of the *monitor*. A windowed client sitting lower than that
  strip (e.g. a custom 1904×816 window on a larger desktop) pushed the label out of the band, so the
  check never fired and the rumour overlay never appeared. The gate is now measured relative to the
  game's own viewport, so it catches the label regardless of where the window sits. Fullscreen and
  borderless are unaffected. (#45)

### Changed

- **OCR pauses while the game isn't in front.** Both the price and rumour scanners now stop capturing
  and OCRing — and hide their overlays — whenever Path of Exile is alt-tabbed or minimised, resuming
  the moment it's back in focus. This cuts idle CPU/GPU use and stops the overlays from floating over
  other apps. If the game window can't be located the app scans exactly as before.

## [3.5.7] — 2026-07-03

### Fixed

- **Prices now load on connections with broken IPv6 (Starlink/CGNAT).** `poe.ninja`/`poecdn`
  publish both IPv6 and IPv4 addresses; on connections where the IPv6 path is a black hole, the app
  used to stall on the dead route until the 15s timeout fired and the whole fetch cycle failed. Network
  connections now use a Happy Eyeballs (RFC 8305) racing connect that falls back to IPv4 in a fraction
  of a second — no firewall rule needed. Healthy connections are unaffected. (#16)

## [3.5.6] — 2026-07-03

### Fixed

- **Items whose name OCR misread as digits now resolve.** Names starting with letters that Windows OCR
  reads as look-alike digits — e.g. **Olroth's** read as *01roth's* (`O`→`0`, `l`→`1`) — no longer come
  back priceless. The leading-noise cleanup was deleting the whole misread first word, and nothing on
  the price-lookup path undid the digit-for-letter swap. The first word is now preserved and a
  digit-fold (`0→o, 1→l, 5→s, 8→b`) recovers the exact name before matching. (#43)

## [3.5.5] — 2026-07-03

### Fixed

- **More localized item names now resolve.** Completed the French locale and filled gaps in the German,
  Russian, and Spanish maps, so items that previously came back unpriced on those clients are matched.

## [3.5.4] — 2026-07-03

### Fixed

- **Corrected wrong and wrong-language localized names.** Several entries across the Russian, German,
  Spanish, and French maps were mistranslated or contaminated with another language's text (e.g. a
  German map holding Spanish words), so those items didn't price on the affected client. Applied
  community corrections and translated the remaining Liquid Verisium fallback. (#42)

## [3.5.3] — 2026-07-01

### Fixed

- **Non-English clients are now read in their own script.** The OCR recognizer is chosen from the
  Settings → Game language you pick, not the Windows profile language. A Russian client on English
  Windows was being read with the Latin recognizer — every Cyrillic name came back transliterated and
  never matched. If the matching OCR language pack isn't installed, the app falls back and logs which
  one to add. (#41)

## [3.5.2] — 2026-07-01

### Fixed

- **The trailing stack-count marker no longer breaks matching.** The exchange panel appends a count
  after the name (`Perfect Chaos Orb (3)`); left on, it corrupted the name lookup — most visibly on
  localized clients, where it defeated the exact translation match. It's now stripped before matching. (#40)

## [3.5.1] — 2026-06-29

### Fixed

- **Non-Latin names survive the leading-noise cleanup.** The routine that strips leading OCR junk was
  ASCII-only, so it erased whole Cyrillic (and other non-Latin) names to nothing and the row was
  dropped as too short. It's now letter-class aware and keeps them. (#39)

## [3.5.0] — 2026-06-28

### Fixed

- **Stack markers glued to the item name are now parsed.** When OCR drops the space in `6xArcanist's
  Etcher`, the quantity is read correctly and the name is no longer mangled.

## [3.4.0] — 2026-06-28

### Added

- **Localized (non-English) client support.** Non-English PoE 2 clients show item names in their own
  language (e.g. German *Chaossphäre*), which never matched poe.ninja's English price keys, so every
  row came back unpriced. A new **Settings → Game language** dropdown (English by default) turns on a
  translation layer that maps the localized name back to English — matching exactly, then with accents
  folded so a dropped diacritic still resolves — before pricing. German, French, Portuguese, Russian,
  and Spanish maps ship seeded from verified in-game names, and you can extend or override them under
  `%LocalAppData%\PoeAncientsPriceHelper\locales\` (see `docs/adding-a-language.md`). Known gap: uncut
  gems, priced per level, aren't translated yet. (#29)

## [3.3.0] — 2026-06-27

### Added

- **Island Rumour helper (experimental).** An optional overlay for the Atlas that OCRs the *Island
  Rumours* panel and shows each rumour's map type, mods, and a community rating in labelled columns,
  displaying the matched name rather than the raw OCR text. It auto-detects the panel while in the
  world, and its confusion-aware matching (down to a skeleton tier for badly garbled reads) copes with
  the stylised font. Ratings come from the community *Expedition Cheatsheet* spreadsheet, which the app
  refreshes on its own (falling back to the bundled copy) — the sheet is linked from Settings and
  credited on the Credits screen. Turn it on, with its own scan-rate, in Settings. (#33–#37)

### Fixed

- **Stack multiplier no longer bleeds between same-item rows.** A stack size read on one row could carry
  over to another row of the same item, showing the wrong total.

## [3.2.1] — 2026-06-27

### Changed

- **Settings button moved next to the donate button**, now with an icon and label, tidying the main
  window's bottom bar.

## [3.2.0] — 2026-06-27

### Added

- **Auto-start.** Once a region is calibrated, opening the app now starts scanning and minimizes to the
  tray automatically — just launch it and it runs, no Start or minimize click needed. Can be turned off
  in the new Settings window. Skipped when launched in debug mode (the window and console stay visible).
- **Settings window.** A gear button on the main window opens a dedicated Settings dialog, moving the
  hotkey rebinds and theme picker off the main screen so it stays clean. It also exposes two options
  that previously had no UI:
  - **Capture mode** — choose between **Auto (GPU + fallback)** and **Legacy (GDI)**. If the overlay
    doesn't appear or prices don't read on your setup, switching to Legacy (GDI) is worth a try. Applies
    on the next start.
  - **Auto-start** — the toggle described above.
- **Diagnostics button.** A "Diagnostics" link restarts the app with logging enabled and writes
  `scan_log.txt` / `debug_ocr.png` to the data folder for bug reports (replacing the old `debug.cmd`,
  which no longer ships with the installer). When already running in debug mode it opens that folder.

### Changed

- **Resilient price fetching.** A fetch that comes back empty (poe.ninja down or blocking) no longer
  wipes the prices you already have — the overlay keeps showing the last good set. The app retries every
  30 seconds until prices return, then goes back to the normal 30-minute cycle. The status line now says
  clearly when a fetch failed (red) or couldn't refresh but is still showing the last set (amber).

### Fixed

- **An all-empty price refresh no longer blanks the overlay.** Previously a single failed refresh could
  overwrite working prices with nothing; the last good prices are now kept instead.

## [3.1.2] — 2026-06-27

### Fixed

- **Switching between two panels now reads the second one immediately.** The ESC / Ctrl+click dismiss
  latch only released after the calibrated region went dark, but brightness can't tell the dismissed
  panel still being open from a different panel now being open — so opening a second panel (or closing
  one into a bright scene) could leave the overlay stuck until something happened to darken the region,
  sometimes seconds later. The latch now releases on content: it keeps scanning while dismissed and
  shows a genuinely different panel right away, while still staying hidden when you Ctrl+click and keep
  working in the same panel.

## [3.1.1] — 2026-06-26

### Fixed

- **Stale prices no longer linger after a panel closes into a bright scene.** The overlay's price rows
  are sticky (an OCR miss never unlocks a locked row), and a panel was only considered closed when the
  screen brightness dropped. Closing the exchange panel into a bright/white map therefore never
  registered as a close, so the previous panel's prices kept showing on a false-positive bright frame
  until enough empty OCR passes cleared them. A confirmed panel that yields no priced row for a few
  passes now clears immediately, independent of brightness, so false positives show nothing until a
  real priced row is read again.
- **Background update downloads are now fully silent.** Reconstructing a release from a delta package
  spawned Velopack's `Update.exe`, which flashed a visible window during the otherwise-silent
  background download (seen on the first delta update, 3.0.0 → 3.1.0). Deltas are now disabled
  (`MaximumDeltasBeforeFallback = -1`), so the app downloads the full package and the only thing the
  user ever sees is the "Update now" link. (Trade-off: each update re-downloads the full package.)
- **The "Update now" link no longer overlaps the theme dropdown and donate button.** All three shared
  a single bottom grid cell, so the (variable-length) update notice drew on top of the bottom bar when
  a release was available. The notice now sits on its own row directly above the bar, and collapses to
  nothing when up to date.

## [3.1.0] — 2026-06-21

### Changed

- **Update checks now run every 30 minutes, not just at startup.** The check piggybacks on the
  existing poe.ninja price-refresh cycle, so a release published while the app is left running (it
  lives in the tray during a play session) is downloaded and staged without a restart — the "Update
  now" link simply appears. The check no-ops once a build is staged and is reentrancy-guarded so it
  can't overlap itself.

### Added

- **Donor thank-you on the Credits screen** — a note thanking everyone who has supported the project
  with a donation.
- **Startup-crash diagnostics.** A top-level handler in `Program.Main` now catches any exception that
  kills the app before it's fully up, writes a full report to `%LocalAppData%\PoeAncientsPriceHelper\crash.log`,
  and shows a dialog pointing the user at that file. Previously a launch-time crash left no trace at
  all — the app is a WinExe (nothing prints to a console) and the `--debug` console attaches too late
  to catch failures in Velopack init or `InitializeComponent()` — making field reports undiagnosable.
  The exception is still rethrown, so the existing exit behaviour is unchanged. (#27)
- **`CONTRIBUTING.md`** — contribution policy: source-only (no prebuilt binaries are reviewed or
  merged), changes land as pull requests rather than issue attachments, and localization / region
  support is accepted as **opt-in, community-maintained data files** (the `custom_prices.json`
  pattern) rather than language-specific tables baked into core.
- **@atomsbaza** added to the in-app Credits screen for #26.

### Internals

- Test coverage for four previously-untested `PriceRepository` paths (#26, **@atomsbaza** 🙏):
  items with a null `primaryValue` are kept as `HasMarketData: false`; `custom_prices.json` keys are
  accepted unnormalized (display names work); a malformed override file is ignored without crashing;
  and a response missing the `core` block falls back to `primary = "divine"`. No production code
  changed.

## [3.0.0] — 2026-06-21

The app now **installs and updates itself**, and **remembers your settings across updates** — no more
re-downloading, re-unzipping, or re-calibrating on every release.

### Added

- **Automatic updates via [Velopack](https://velopack.io/)** (MIT), served straight from this repo's
  GitHub Releases — no separate update server. On startup the app checks for a newer release and
  downloads it in the background; an **"Update now"** link installs + relaunches instantly, or — if
  ignored — the staged update is **applied silently when the app is next closed**, so the following
  launch is already up to date.
- **Per-user installer** (`Setup.exe`) replacing the portable zip; installs under `%LocalAppData%`,
  no admin required.

### Changed

- **Settings now persist in `%LocalAppData%\PoeAncientsPriceHelper`** (calibration, league, hotkeys,
  theme) instead of next to the executable, so updates never wipe them.
- README documents the installer flow, how auto-update works, and third-party acknowledgements
  (Velopack/WPF UI/SharpHook/Vortice/Newtonsoft, all MIT).

### Migration

- One-time only: installing v3.0.0 over an old unzipped v2.x build means re-picking your league and
  re-calibrating **once** (settings moved to a stable location). After that, updates are automatic and
  your settings carry across them.

### Internals

- Explicit `Program.Main` runs `VelopackApp.Build().Run()` before app startup; the "Update now" and
  on-exit apply paths were verified end-to-end against a local Velopack feed (including the
  relaunch-vs-single-instance-mutex interaction).

## [2.2.0] — 2026-06-21

Based on community PR #24 by **@exploitz86**. 🙏

### Fixed

- **Stacked items no longer flicker between unit price and total** (#24) — Windows OCR
  intermittently drops the leading `Nx` quantity marker on a stacked row for a frame, which made the
  price label oscillate between the per-unit price and the stack total. Quantity reads now carry an
  *explicit-vs-assumed* flag, and a short-lived (1.5s) per-item memory keeps a known stack's
  multiplier through a pass that misreads it as `1x`, instead of snapping back. (Thanks @exploitz86.)

### Added

- **Contributors credits screen** — a **Credits** link beside the version (bottom-left of the
  settings window) opens a small dialog thanking community contributors, with links to their GitHub
  profiles. The list is a curated static array (not the GitHub contributors graph) so PR authors
  whose work was reimplemented under maintainer commits are still credited. Follows the selected
  theme; adding a contributor is a one-line edit.

### Notes

- PR #24 also bundled a second per-frame OCR pass, halved scan intervals, a confidence-gated render
  filter, and scroll heuristics. These were **left out** — they raise the always-on overlay's CPU/GPU
  load substantially and the render filter could suppress legitimate fuzzy-matched single-item prices.
  Only the stack-quantity fix was kept and re-implemented with unit tests (96 tests pass). A security
  review of the contribution found no new network calls, process execution, file writes, reflection,
  or native interop — the change is confined to the OCR-to-display path.

## [2.1.0] — 2026-06-21

A community release built almost entirely on PR #23 by **@dhaern (Raxxoor)**. 🙏

### Added

- **Modern settings UI** — migrated from MahApps.Metro to **WPF UI (lepoco)** for a native, actively
  maintained Fluent / Windows 11 look on .NET 10.
- **Theme switcher** — 5 dark themes from the settings window (Toxic [default], Midnight, Obsidian,
  Abyss, Ember). Only the window background changes; button colors are untouched. Saved in
  `config.json`.

### Fixed

- **Items with no trading data no longer show a false "0 divines"** — poe.ninja returns no value for
  items with no market data; these were treated as a real price of 0 and could surface as "0.00 d".
  Such items are now shown as a dim **"no info"** instead, so a known item with no price can't
  masquerade as a free one. (Thanks @dhaern.)

### Notes

- PR #23 also added a font selector with several bundled fonts. It was **left out for licensing
  reasons** — a redistributable download can only bundle redistributable fonts, and Angie SmallCaps
  (commercial) and Fontin SmallCaps (requires a paid app license) don't qualify. The overlay keeps
  its built-in Consolas. A full security + quality review of the contribution was run before merging
  (no new network/process/file-write behavior; bundled font files binary-inspected; 87 tests passed).

## [2.0.1] — 2026-06-19

### Fixed

- **Overlay oversized / offset on a high-DPI secondary monitor** (#21) — on a multi-monitor setup
  where PoE runs on a monitor at a non-100% display scale (e.g. 125%) while another monitor is at
  100%, the price overlay (and the F3 debug boxes) were drawn ~1.25× too large and shifted. The v2.0
  layered-window overlay composes its scene in physical pixels and blits via `UpdateLayeredWindow`,
  which is only 1:1 if the overlay window is genuinely Per-Monitor-V2 aware; on the scaled monitor
  its effective DPI context was lower, so DWM upscaled the whole bitmap. Capture/calibration use raw
  physical APIs and were unaffected, so prices were read correctly but drawn in the wrong place. The
  overlay thread now pins `PER_MONITOR_AWARE_V2` before its window is created, and the form opts out
  of WinForms auto-scaling (`AutoScaleMode.None`). (Regression from the PR #20 rewrite; same class as
  the mixed-DPI fix in #8.)

### Packaging

- **`debug.cmd`** added to the distributable — double-click to run with diagnostics (console + a
  detailed `scan_log.txt`) for problem reports, without typing `--debug` by hand. README updated.

## [2.0.0] — 2026-06-19

A ground-up performance and stability overhaul. The app now uses a fraction of the CPU it used to,
captures frames via the GPU, and detects items instantly with the native Windows OCR engine.

### OCR

- **Replaced Tesseract with Windows.Media.Ocr** — the native WinRT OCR engine designed for on-screen
  text. Tesseract (a document-oriented engine) struggled with rendered game text; some items took
  seconds to appear or were never detected. Windows OCR detects all items instantly with no external
  dependencies (no traineddata files, no NuGet packages).
- **3x upscaling** for glyph accuracy on small fonts.

### Performance

- **Windows Graphics Capture (WGC) backend** — screen capture now runs on the GPU via D3D11 +
  WinRT interop (Vortice), cutting CPU usage dramatically compared to GDI `CopyFromScreen`.
  Falls back to GDI automatically per-frame if WGC is unavailable. Configurable via
  `CaptureBackend` in `config.json` (`"Auto"` / `"GDI"`).
- **Overlay render skip** — the overlay no longer repaints every cycle; it only redraws when the
  rows, panel state, or reading state actually change.
- **Cached render buffer** — avoids allocating a monitor-sized bitmap on every render.
- **Panel detection via LockBits** — `ListDetector` now uses `LockBits` + `Marshal.ReadByte`
  instead of 60 individual `GetPixel` calls per pass.
- **Resolution cache** — OCR'd names are resolved to price keys once, then cached (invalidated on
  each price refresh). Skips the dictionary scan + Levenshtein work on every subsequent pass.
- **Length-bucketed fuzzy index** — the fuzzy matcher only scans price keys within ±3 characters
  of the OCR'd name length, not the entire dictionary.
- **Pre-compiled regexes** — all `Regex` instances in the hot path are now `static readonly`
  with `RegexOptions.Compiled`.
- **Parallel price fetch** — all exchange types are fetched concurrently via `Task.WhenAll`
  over a single HTTP/2 connection instead of sequentially.
- **Throttled scan intervals** — OCR runs at 150ms (was 100ms) while the panel is open; idle
  polling runs at 300ms (was 150ms). Both still feel instant while cutting CPU load significantly.

### Stability

- **Overlay concurrency fixes** — `PriceOverlayManager` no longer holds a lock across cross-thread
  UI dispatches (deadlock risk), and all UI calls are wrapped in try/catch for
  `ObjectDisposedException` / `InvalidOperationException`.
- **League-switch lifecycle** — changing leagues now stops and disposes the scanner before
  reloading prices/icons, with a reentrancy guard to prevent overlapping reloads.
- **Atomic price snapshot** — `Prices` and `KeysByLength` are now published together in a single
  immutable `PriceSnapshot` record, preventing torn reads during background refresh.
- **Static flag reset** — `_dismissed` / `_showing` are reset in `Start()` so a stale loop can't
  clobber a new instance's state.
- **GDI handle leak** fixed in overlay rendering (`GetDC` / `CreateCompatibleDC` now inside `try`).
- **WGC resource management** — COM reference leaks fixed in `GraphicsCaptureItem` creation and
  `ID3D11Texture2D` frame access. Frame pool is recreated on monitor resolution change. WGC
  permanently falls back to GDI if initialization fails (no retry storm).
- **Stale overlay clearing** — the overlay automatically hides stale prices during loading screens.

### Fixed

- **Freeze when calibrating via the hotkey (#14)** — triggering calibration with the global hotkey
  while the game (or any other app) held the foreground left the calibration overlay stuck behind it
  with no way to confirm/cancel, blocking the UI thread indefinitely. The overlay now forcibly takes
  the foreground when shown (`AttachThreadInput` + `SetForegroundWindow`).
- **Per-Monitor-V2 DPI awareness kept in `app.manifest`** — DPI awareness must be declared in the
  manifest for this WPF-first app; the `<ApplicationHighDpiMode>` project property is a no-op here
  and would have regressed the mixed-DPI multi-monitor fix from #8.

### Code quality (YAGNI)

- Removed dead code: `ScreenCapture.cs`, `IsAllBlack`, `ReferencePixelColor`.
- Unified duplicate `NormalizeName` into a single `NameNormalizer` helper.
- Simplified `ListDetector` to return only the sampled color (threshold logic lives in `ScanEngine`).
- Extracted `WithForm(...)` helper in `PriceOverlayManager` to eliminate 5× duplicated lock+try/catch.
- Extracted `DisposeDevice()` in `WgcScreenCaptureBackend` to eliminate duplicated teardown.
- Removed empty `IDisposable` from `OcrScanner`, dead `Stop()` method, and one-line wrappers.

### Dependencies

- Target framework bumped to `net10.0-windows10.0.19041.0` (.NET 10).
- Added `Vortice.Direct3D11` and `Vortice.DXGI` for WGC interop.
- Removed `Tesseract` and `Tesseract.Data.English`.
- All other NuGet packages updated to latest stable.

## [1.1.8] — 2026-06-14

### Fixed
- Calibration on **mixed-DPI multi-monitor** setups (e.g. a 100% primary + a 175% secondary).
  Calibrating on the high-DPI monitor used to produce a wrong region — size shrunk by the scale
  factor, origin off-screen — so the overlay showed nothing. The app now declares Per-Monitor-V2 DPI
  awareness at startup and captures the calibration box in true physical screen pixels, so the region
  matches exactly where you drew it. (#8, reported & verified by @ljere)
- Calibration instructions now stay pinned to the primary monitor instead of occasionally rendering
  on the secondary screen.

## [1.1.7] — 2026-06-12

### Changed
- Price fetches now time out after 15s and retry on the next cycle instead of blocking for up to
  100s when poe.ninja is slow or a connection stalls.
- The config file is now written atomically, so a crash or power loss mid-save can no longer corrupt
  it and reset your region/hotkeys to defaults.
- The diagnostic `debug_ocr.png` dump is only written in debug mode, so normal users no longer get a
  file rewritten in their folder while a panel is open.
- Removed dead code and tightened shutdown so an in-flight fetch is cancelled cleanly.

Most of this hardening came out of a community code audit (thanks to @crichmond1989).

## [1.1.6] — 2026-06-12

### Added
- **Uncut gem prices** — uncut skill, spirit, and support gems are now priced by exact gem type
  **and level** (e.g. `Uncut Spirit Gem (Level 19)` priced as a level-19 spirit gem). A row shows
  **?** rather than a guessed price if the type or level can't be read cleanly, then fills in once a
  clean read arrives — neighbouring levels can differ several-fold in value.
- **Update notifications** — on startup the app checks GitHub for a newer release and shows an
  "Update available" link in the window when one exists.

## [1.1.5] — 2026-06-12

### Added
- **Configurable hotkeys** for Start/Stop, Debug overlay, and Calibrate, each independently
  rebindable (defaults unchanged: F5 / F3 / F4). Binding a key already used by another action is
  rejected. (#4, #6)
- **Minimize to tray** — minimizing sends the app to a system-tray icon and keeps scanning in the
  background; double-click (or right-click → Show) to restore, right-click → Exit to quit. (#2)
- **Multi-monitor support** — calibrate the region on any monitor, and the overlay appears on the
  monitor your game runs on instead of only the primary one. (#3)

### Fixed
- Overlay flicker — fixed a post-Escape re-flash where prices briefly reappeared after closing the
  panel, and added brightness hysteresis so the overlay no longer blinks near the detection
  threshold. (#5)

## [1.1.4] — 2026-06-09

### Changed
- Each price now sits on a subtle semi-transparent dark plate behind the icon and number, so values
  stay legible over busy in-game art. The overlay was reworked to true per-pixel transparency.

## [1.1.3] — 2026-06-08

### Added
- **Configurable Start/Stop hotkey** — F5 is now just the default; rebind it in-app and it takes effect
  immediately and persists.

### Changed
- Start/Stop now uses the same global key listener as the other hotkeys, so it no longer clashes with
  other apps that grab the same key.

## [1.1.2] — 2026-06-08

### Added
- **Hardcore league pricing** — the League dropdown now offers **HC Runes of Aldur**, with prices
  pulled from the matching poe.ninja economy and correctly denominated (softcore in divine, hardcore
  in exalted).
- The running version is now shown in the bottom-left of the window.

## [1.1.1] — 2026-06-08

### Added
- F5 hotkey to start/stop scanning without clicking the window.

### Fixed
- Prices now always use a `.` decimal separator on every system locale (previously showed e.g.
  `0,1` on comma-decimal regions).

## [1.1.0] — 2026-06-08

First public release.

### Added
- Live poe.ninja prices overlaid on the in-game currency list.
- One-time calibration; stack-aware pricing (e.g. `2 (0.5 each)`).
- Self-contained Windows x64 build.

[3.1.0]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v3.1.0
[3.0.0]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v3.0.0
[2.0.0]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v2.0.0
[1.1.8]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v1.1.8
[1.1.7]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v1.1.7
[1.1.6]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v1.1.6
[1.1.5]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v1.1.5
[1.1.4]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v1.1.4
[1.1.3]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v1.1.3
[1.1.2]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v1.1.2
[1.1.1]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v1.1.1
[1.1.0]: https://github.com/pedro-quiterio/PoeAncientsPriceHelper/releases/tag/v1.1.0
