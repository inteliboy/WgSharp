## Approach
- Read existing files before writing. Don't re-read unless changed.
- Thorough in reasoning, concise in output.
- Skip files over 100KB unless required.
- No sycophantic openers or closing fluff.
- No emojis or em-dashes.
- Do not guess APIs, versions, flags, commit SHAs, or package names. Verify by reading code or docs before asserting.

## Dark/light theme (AppTheme.cs, NativeMethods.cs, ThemedControls.cs, Dialogs.cs)
Ported from `C:\Users\inteliboy\Desktop\LenovoRepoBuilder`'s `Theme.cs`/`NativeMethods.cs`/`Dialogs.cs`/`ThemedControls.cs`
(see that project's CLAUDE.md "Visual overhaul" section for the deeper native-control history — most of the
workarounds below are direct ports, not rediscovered). A prior attempt at dark mode here was ripped out
entirely (see the old `AppTheme.cs` doc comment) because native WinForms controls silently ignore
`BackColor`/`ForeColor` for parts of themselves under visual styles; this pass fixes that class of bug instead
of working around it per-control ad hoc.

- **`AppTheme`** (`src/ui/AppTheme.cs`) is the single palette: `IsDark` is a mutable static bool (defaults to
  the real OS setting via `HKCU\...\Personalize\AppsUseLightTheme`), every color is a live-read property (not
  cached), so a runtime toggle only needs `Invalidate()`, never per-instance recoloring. The original 9 members
  (`WindowBg`/`PanelBg`/`GroupText`/`FieldLabel`/`FieldValue`/`ListBg`/`ListSelBg`/`ListSelText`/`LogBg`) kept
  their names so none of the ~60 pre-existing call sites needed renaming — only their *values* gained a dark
  branch. New members (`Accent`, `Border`, `Surface`, `EntryBg`/`EntryFg`, `LogFg`, `Err`/`Warn`,
  `Plot*`, `Syntax*`) exist because something concrete consumes them — check before adding another, unused ones
  get deleted on sight per this file's own rule.
- **Persistence**: `AppSettings.ThemeIsDark`/`ThemeOverrideSet` (same registry/file split as every other
  setting). `ThemeOverrideSet` starts false (follow the OS setting); the Settings tab's "Dark theme" checkbox
  sets it true permanently on first toggle — matches LenovoRepoBuilder's own "no way back to auto" precedent
  once a user has expressed a preference. `Program.cs`'s `RunApp()` applies the override and calls
  `NativeMethods.SetPreferredAppMode`/`FlushMenuThemes` *before* `Application.EnableVisualStyles()` — ordering
  matters, confirmed by the Lenovo port this was copied from.
- **Toggle UI lives in `SettingsPanel`, not a title-bar button** — unlike Lenovo/UUPDumpGUI, `MainForm` has no
  top bar (`tabs` docks `Fill` directly under the Form); adding one just for a sun/moon button would be a real
  layout change for one control. `SettingsPanel.MakeOption`'s existing checkbox factory already builds 7
  options identically; switching it from `CheckBox` to `ThemedCheckBox` themed all 7 for free, and the 8th
  ("Dark theme") cost nothing extra. `SettingsPanel.ThemeToggled` (event) → `MainForm.ApplyTheme` (subscribed
  in the constructor, same pattern as `PortableModeChanged`).
- **`MainForm.ApplyTheme()`** (renamed from the old one-shot `ApplyStaticTheme()`) is now re-invocable: called
  once at construction and again on every toggle. Recolors backgrounds, re-flattens the 6 toolbar buttons,
  re-applies the dark title bar / native scrollbar theme, calls `BuildDetail()` (cheap, already guards
  `_config == null`) and `_statsPanel.RefreshTheme()`/`_settingsPanel.RefreshTheme()` for the two panels whose
  own `BackColor` was snapshotted once at construction rather than read live.
- **Tab strip** (`tabs`): owner-drawn (`DrawTab`) + 6 overlay panels (`_tabRowFill`/`_tabFrameTop/Bottom/Left/Right`/`_tabGroupTop`)
  plus a generalized `_tabInterGapFills[]` (one per gap, loops over however many tabs exist — this app has 4:
  Tunnels/Stats/Settings/Log) built once in `BuildTabOverlays()`, laid out from `tabs.GetTabRect(...)` on every
  `tabs.Resize`. Directly ported from Lenovo's `BuildTabOverlays`/`DrawTab` — native `TabControl` always paints
  its header via visual styles (light, regardless of `BackColor`) otherwise; this is the single most visible
  gap if skipped, since the tab strip is the first thing on screen.
- **Buttons**: every `Button` in this app was a plain `new Button()` with `UseVisualStyleBackColor = true` and
  no `FlatStyle` — a materially different starting point than Lenovo (whose buttons were always built via a
  `Ctrl.NormalButton`/`AccentButton` factory with `FlatStyle.Flat` from construction). Native non-flat buttons
  ignore `BackColor` under visual styles, so `Ctrl.FlattenButton(Button, bool accent)` (`AppTheme.cs`) is
  applied to already-constructed buttons (from `ApplyTheme()`/each dialog's constructor) instead of replacing
  their construction — same end state, adapted to how this codebase actually builds controls.
- **`ThemedCheckBox`** (`ThemedControls.cs`): ported as-is from Lenovo — native `CheckBox` always paints its
  13x13 check glyph via UxTheme regardless of `FlatStyle`/`BackColor`, confirmed there live, not re-verified
  here (same OS-level quirk, no reason it would differ).
- **`ThemedGroupBox`** (`ThemedControls.cs`): **new code, no Lenovo precedent** — that project has zero
  `GroupBox` usage. Deliberately does NOT reproduce native `GroupBox`'s boxed border with a gap cut for the
  label (a fiddly recipe this project had no working reference to verify against); instead draws a caption +
  underline, which groups content just as clearly and is far less error-prone. Supports both usage styles in
  this codebase — `Dock=Top`+`AutoSize`+`Padding` (`MainForm`'s Interface/Peer sections) and fixed
  `Location`/`Size` (`StatsPanel`'s Summary box) — through one `DisplayRectangle` override (the officially
  supported "reserve extra layout space beyond `Padding`" extension point), so both call sites need zero
  special-casing.
- **`ThemedMessageBox`** (`Dialogs.cs`): ported as-is, drop-in `MessageBox.Show` replacement (same signature).
  Every `MessageBox.Show(...)` call site in the UI was switched to it. The one exception, left deliberately
  native: `Program.cs`'s "WgSharp is already running" mutex-check dialog — it fires before `RunApp()` even
  starts (before any per-run theme setup is meaningful), same as it always did.
- **`lstTunnels`** wrapped in a themed 1px border panel (`Ctrl.Bordered`, `MainForm.Designer.cs`'s
  `lstTunnelsBorder`) — native `BorderStyle.FixedSingle` always renders a fixed color with no settable
  property. `txtLog` already used `BorderStyle.None` (no border to theme, nothing to change there).
- **`EditConfigDialog`**'s syntax-highlight palette (`ColSection`/`Key`/`Value`/`Comment`/`Default`) moved from
  local hardcoded `Color` fields into `AppTheme.Syntax*` — the light values are unchanged from before; the dark
  values are the familiar VS Code dark-theme syntax colors (blue/purple/cyan/green/light-gray), chosen for
  known-good contrast on a near-black background rather than guessed.
- **Intentionally NOT themed** (functional, not a gap): `QrDialog`'s QR image panel and `QrScanDialog`'s camera
  preview stay fixed white/black (a QR code needs a white quiet zone to scan; the preview is a live video feed)
  — only their window chrome (dark title bar) follows the theme. Shield status colors
  (green/red/amber/gray in `MainForm.DrawShield`/`OnTrayMenuOpening`), `Icons.Add`/`Delete`, and `AreaChart`'s
  two line colors stay fixed brand/semantic colors, matching Lenovo's own precedent of leaving
  `Accent`/`Danger` unthemed. `Icons.Export`/`QrGlyph` DO take a theme color now (`AppTheme.FieldValue`) — the
  old fixed dark-gray fill was near-invisible on a dark button surface; only those two needed it since
  Add/Delete's green/red already have enough contrast on both themes.
- **Verified live**: built via `build.cmd`, launched the real exe (`asInvoker`, no UAC needed), screenshotted
  both themes and a runtime toggle (Settings checkbox flips title bar + tab strip + every panel instantly),
  and the About dialog. Not re-screenshotted per-pixel the way LenovoRepoBuilder's CLAUDE.md documents (no
  defect required that level of forensics here) — if a future change needs it, that project's "Verification
  method" note about a near-white-region false-black illusion in this tool's own image preview still applies.

## QR-scan import (QrImageLocator.cs, WebcamCapture.cs)
The fixes below were verified two ways: (1) a standalone synthetic round-trip test harness (encode with
`QrCode.Encode` → render to a `Bitmap` → rotate/resize with GDI+ to simulate a hand-held photo → decode with
`QrImageLocator.TryDecodeFrame` → compare against the original text), compiled and run directly with
`csc.exe` against the real source files, no project changes needed to run it; (2) live in the actual app via
both the "Scan from image file…" fallback and the live webcam path in `QrScanDialog`, round-tripping a QR
the app's own "Show QR" exported. Both found and fixed real bugs; both are documented here since neither is
obvious from reading the code cold.

- **`QrImageLocator.cs` — the module-count ("QR version") guess was fragile under any nontrivial rotation,**
  confirmed as the actual cause of realistic scan failures (not a hypothetical): a QR held even a few degrees
  off-axis (utterly normal for someone holding a phone up to a camera) has its finder-pattern "module size"
  measured too large, because `FindFinderCandidates`'s horizontal/vertical run-length scan cuts across a
  tilted pattern at an angle — a few-percent bias that, combined with `Math.Round`'s round-half-to-even
  landing a borderline case on the wrong side, was enough to guess one whole QR version off. That's fatal on
  its own even with perfect finder-center positions: every fixed-position element (format info, alignment/
  timing patterns, the codeword zigzag) then reads from the wrong place, so no amount of Reed-Solomon
  correction downstream can recover it. Fixed by trying a small window of sizes (the best guess, plus its
  immediate smaller/larger neighbor) per finder triple instead of committing to one — `SampleGrid` became
  `SampleGrids`/`SizeCandidates` (`QrImageLocator.cs`). Confirmed via the synthetic harness: forcing the
  correct size decoded successfully every time the single best-guess size had failed.
- **`QrImageLocator.cs` — a finder pattern's X position was only ever estimated once (the raw horizontal-scan
  cluster centroid) and never re-refined,** while its Y position WAS refined via `VerticalCrossCheck`
  (scanning back down a column at the estimated X) — an asymmetry with no reason behind it. Under rotation
  this left some fraction of a module of X error uncorrected, and for dense/large (high-version) QR codes
  specifically, that was enough by itself to make an otherwise-correctly-sized sampled grid ~30-50% wrong
  per-cell (confirmed by diffing a sampled grid against the known-correct encoder grid in the test harness) —
  nowhere near what Reed-Solomon can fix, despite the *size* guess being completely correct. Fixed by adding
  `HorizontalCrossCheck` (mirrors `VerticalCrossCheck`, scanning the refined Y row to re-estimate X) and
  calling it after Y is refined, in `FindFinderCandidates`. After both fixes, a 60-case randomized stress test
  (random rotation -15°..15°, random scale, random resize, short and long/high-version payloads) passed
  58/60; the 2 remaining failures are both the same extreme combination (version-30 payload at the smallest
  tested scale *and* 10°+ rotation) — realistic WireGuard configs are nowhere near version 30, so this was
  left as a documented soft edge rather than chased further.

## Webcam capture (WebcamCapture.cs)
Drives the webcam by hand-building a DirectShow capture graph via raw COM interop (device source filter →
Sample Grabber → Null Renderer), adapted from secile's `UsbCamera.cs` (MIT license,
`https://github.com/secile/UsbCamera`) and trimmed to only what WgSharp needs, then rewritten for C# 5 (no
auto-property initializers, no local functions — this project's build only supports up to C# 5). No external
packages, matching this project's zero-dependency, direct-`csc.exe` build — .NET Framework 4.8 has no managed
DirectShow wrapper, so every interface/struct/GUID is hand-declared, with method lists trimmed to the prefix
each interface actually needs (a `[ComImport]` interface only needs its declared methods to match the real
vtable's *prefix*; unused trailing/interior slots are kept as placeholder `_Unused()` methods purely to hold
the correct vtable position).

- **No dedicated capture thread.** Building/tearing down the graph uses COM objects that expect an STA
  apartment, which the WinForms UI thread already is (`[STAThread]` on `Main()`), so `Start()` runs
  synchronously on the caller's thread. Frame delivery (`ISampleGrabberCB.BufferCB`) is invoked by
  DirectShow's own internal streaming worker thread regardless of which thread built the graph; the callback
  itself only copies bytes under a lock, which needs no COM apartment at all.
- **`IMediaControl` must be declared `InterfaceType.InterfaceIsDual`, not `InterfaceIsIUnknown`.** DirectShow's
  control interfaces (`IMediaControl`, `IVideoWindow`, `IBasicAudio`, `IBasicVideo`, `IMediaEventEx`) are
  *dual* interfaces (IDispatch + vtable), unlike most of DirectShow's other interfaces, which are pure
  IUnknown. Declaring one of these as `InterfaceIsIUnknown` shifts every call by IDispatch's 4 extra vtable
  slots (`GetTypeInfoCount`/`GetTypeInfo`/`GetIDsOfNames`/`Invoke` sit between `IUnknown`'s 3 and the
  interface's own methods for a dual interface), so e.g. `Run()` would silently call the real
  `GetTypeInfoCount` instead. This kind of mismatch doesn't throw or fail loudly — it silently calls the
  wrong method and can still return a plausible-looking non-negative value, so the graph can appear to build
  correctly while never actually transitioning to the Running state and delivering zero frames. When adding
  interop for any other dual COM interface in this codebase, check how a proven reference implementation
  declares that *same* interface rather than inferring `InterfaceIsIUnknown` by analogy with neighboring
  interfaces in the same API.
- **`IMediaControl.Run()` can return `S_FALSE`**, meaning the Stopped→Running transition is happening
  asynchronously — `Start()` follows up with `GetState(3000, out state)` in that case rather than assuming
  frames are already flowing the moment `Run()` returns without error.
- **RGB24 output**: the Sample Grabber is configured to request `MEDIASUBTYPE_RGB24` regardless of the
  device's native format, so DirectShow's own intelligent-connect handles any needed colorspace conversion
  internally; `GrabFrame()` is a straight bottom-up-DIB row copy into `Bitmap(w, h, Format24bppRgb)`, no
  per-pixel channel math.
- **Known failure mode**: on Windows 10/11, desktop (Win32) app camera access can be blocked system-wide by
  Settings → Privacy & security → Camera ("Camera access" / "Let desktop apps access your camera"). When
  that's off, the graph can still build and run successfully while the driver delivers only black frames or
  none at all. `QrScanDialog` detects both (no frames arriving, or several consecutive blank frames) and
  points the user at that setting via an "Open camera privacy settings…" button.
- **Verified**: `build.cmd` builds clean. Confirmed end-to-end via a standalone harness compiled directly
  against `src/ui/WebcamCapture.cs` (`FrameCount` incrementing, `GrabFrame()` returning a correctly-oriented
  bitmap) and via the actual shipped app (`WgSharp.exe` → Add Tunnel → Scan from QR code → the dialog's live
  preview shows real, moving video).

## Installer (installer\WgSharp.nsi, installer\CloseWgSharp.ps1)
Replaces the old WiX/MSI installer (`installer\Product.wxs`, removed — its own doc comment has the fuller
MSI-specific history, e.g. the `AllowSameVersionUpgrades` same-day-rebuild issue). MSI's declarative
component/table model kept fighting this project's date-based same-day version stamps and its
"close the app the right way" requirement; NSIS's scripted flow expresses both far more directly. Built via
`makensis.exe` from `build.cmd`, same "no separate project file, one script" philosophy as the rest of the
build.

- **No "choose install folder" page**: fixed at `$PROGRAMFILES64\WgSharp`, matching
  `InstallLocation.ExpectedInstallDir` (`Environment.SpecialFolder.ProgramFiles` from a 64-bit process
  resolves to the same real `Program Files`, not the x86 one) — same reasoning the old MSI's own comment
  gave: a movable install location would break that detection, which is what lets WgSharp auto-enable
  autostart/the service and disallow Portable mode on first run.
- **Closing a running WgSharp is NOT a plain `taskkill`/WM_CLOSE.** `MainForm.OnFormClosing` only treats
  `CloseReason.UserClosing` (what an ordinary WM_CLOSE produces) as "minimize to tray, don't exit" — so a
  bare `taskkill` or `Process.CloseMainWindow()` gets silently swallowed, same trap the old MSI's own
  `util:CloseApplication` comment already documented. `installer\CloseWgSharp.ps1` sends
  `WM_QUERYENDSESSION` + `WM_ENDSESSION` instead (via a small `Add-Type`-compiled `EnumWindows`/
  `SendMessageTimeout` helper), which .NET WinForms maps to `CloseReason.WindowsShutDown` — a reason
  `OnFormClosing` already treats as "really exit," running its synchronous tunnel teardown. The script also
  stops `WgSharpSvc` first (`Stop-Service` + wait) since it's the *same* `WgSharp.exe` running headless as
  SYSTEM in Session 0 — `Get-Process -Name WgSharp` matches both roles, so every step filters on
  `SessionId -ne 0` to isolate the interactive GUI instance(s) from the service process. A force-`Stop-Process`
  fallback after a 15s timeout guarantees the exe is unlocked regardless, matching the old MSI's
  `TerminateProcess="1"` belt-and-suspenders. Bundled into both the installer and uninstaller via `File`
  (extracted to NSIS's own auto-cleaned `$PLUGINSDIR`, never left in `$INSTDIR`), so the same script backs
  `CloseRunningWgSharp` (install) and `un.CloseRunningWgSharp` (uninstall).
- **Legacy MSI detection/removal** (`RemoveLegacyMsiInstalls`, install-only): the old MSI's `Product Id="*"`
  minted a fresh ProductCode on every build, so there's no single GUID to hardcode. Instead it enumerates
  `HKLM\...\Uninstall`, filters to brace-GUID key names (`{...}` — an MSI ProductCode; this installer's own
  key is named plainly `WgSharp`) whose `DisplayName` is `WgSharp`, and runs `msiexec /x <GUID> /qn /norestart`
  on each, restarting the enumeration from 0 after every removal since a deleted subkey shifts every later
  index down. Never touches `ProgramData\WgSharp\conf` or `HKLM\Software\WgSharp` — the old MSI's own
  component set never covered either (see `ConfigStore.cs` / `AppSettings.cs`), so `msiexec /x` is exactly as
  safe here as it always was; install, update, AND uninstall all leave a user's saved tunnels and settings
  completely untouched.
- **Service management** (`InstallService` / `un.UninstallService`): mirrors `ServiceInstaller.Install()`
  exactly — `sc create`/`sc config` with `binPath= "\"<exe>\" --service"` (the same quoting
  `ServiceInstaller.BinPathValue` builds), `start= auto`, `obj= LocalSystem`. Registered but never started
  (idles until a tunnel is activated, or reconnects one at boot from its own persisted state), same as the
  old MSI. Because this runs unconditionally on every install-section execution (fresh install or update,
  no MSI-style component-version skip logic), the "service registration got wiped by an upgrade" gap
  `InstallLocation.RestoreServiceIfUpgradeWipedIt` exists to patch is no longer the routine case here — see
  that method's own doc comment.
- **Wizard banners** (`installer\banner-header.bmp`, `installer\banner-welcome.bmp`, `installer\banner-welcome-dark.bmp`):
  generated once from the same 256×256 PNG-compressed frame `AppIconLoader.cs` reads out of the exe's own
  embedded icon resource at runtime, via a throwaway PowerShell + `System.Drawing` script (not checked in —
  regenerate by parsing `WgSharp.ico`'s `ICONDIR`/`ICONDIRENTRY` records directly for the largest frame, same
  technique `AppIconLoader.LoadLargestEmbeddedIcon` uses, then compositing onto 150×57 / 164×314 canvases with
  `HighQualityBicubic` scaling — NSIS header/welcome images must be actual `.bmp`, not `.ico`/`.png`).
  Regenerate all three if `WgSharp.ico` changes; they're the installer/uninstaller icon
  (`MUI_ICON`/`MUI_UNICON`) and the header/sidebar bitmaps (`MUI_HEADERIMAGE_BITMAP`/`MUI_WELCOMEFINISHPAGE_BITMAP`).
  Both banners carry a two-tone "WgSharp" wordmark — "Wg" in WireGuard's own brand red (`#88171a`, sampled
  directly from wireguard.com's logo SVG, not guessed — the original project this client reimplements), "Sharp"
  in this logo's own orange (`#ef972f`, sampled directly from `WgSharp.ico`'s own pixels) — drawn with
  `StringFormat.GenericTypographic` (not GDI+'s default `GenericDefault`, which pads extra side bearing around
  each string and left a visible gap between "Wg" and "Sharp" instead of reading as one word).
  - **Header** (`banner-header.bmp`, one file, used in BOTH themes — no dark variant): icon on the left (8px
    margin), wordmark to its right, font size auto-picked from 13pt down to 9pt until it fits the space right
    of the icon. Confirmed by cropping a real screenshot pixel-by-pixel (not assumed) that this bitmap control
    (ID 1046) is a separate, fixed-width region from the page title/subtitle text, which lives in its own
    unrelated white background panel further right — no overlap risk, and no reason to darken this one for
    consistency with anything else on that strip, hence staying white in both themes per explicit direction.
  - **Welcome/finish** (`banner-welcome.bmp` light / `banner-welcome-dark.bmp` dark): icon centered, wordmark
    below it, full-bitmap background color swaps with the theme — white for light, `#1a1a2e` (matching
    `AppTheme.WindowBg`'s own dark value) for dark, with "Wg" brightened to `#ff5252` in the dark variant since
    the deep WireGuard red turns muddy/low-contrast against that navy (the orange needs no adjustment, already
    bright enough for both fields).
- **Dark/light theme**: the installer follows the CURRENT USER's OS preference (`HKCU\Software\Microsoft\
  Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme` — the same value `AppTheme.cs`'s `IsDark`
  static initializer reads), detected once in `.onInit`/`un.onInit` (before any window exists) into
  `$IsDarkMode`, then applied once in `WgSharpApplyDarkMode`/`un.WgSharpApplyDarkMode` — wired up via
  `!define MUI_CUSTOMFUNCTION_GUIINIT`/`_UNGUIINIT`, MUI2's own documented "run custom code once the main
  window exists" hook. What gets themed: the window's title bar (`DwmSetWindowAttribute`
  `DWMWA_USE_IMMERSIVE_DARK_MODE`, attribute 20 falling back to 19) and the welcome/finish sidebar bitmap
  (re-extracted over `$PLUGINSDIR\modern-wizard.bmp` with `banner-welcome-dark.bmp`) — the exact same
  `uxtheme.dll`/`dwmapi.dll` calls `src\ui\NativeMethods.cs` uses for the app's own window, ported via
  `System::Call` since NSIS has no managed P/Invoke layer. **The header banner stays white in both themes**
  (see above) and **deliberately does NOT theme the native Win32 controls** (License page text box, buttons,
  progress bar stay standard system light) — reliably dark-theming those needs per-control subclassing
  (`WM_CTLCOLOR*` interception, owner-drawn buttons) that's normally done via a dedicated compiled plugin
  (e.g. the community "NsisDarkMode" plugin), a much larger and more fragile undertaking than this installer
  warrants; title bar + welcome banner get it out of the jarring "pure white window on an otherwise dark
  desktop" look without that risk.
  - **Why re-extracting the welcome bitmap works, verified from NSIS's own installed source on this machine
    (not assumed)**: `Contrib\Modern UI 2\Interface.nsh`'s `MUI_FUNCTION_GUIINIT` macro generates
    `.onGUIInit` itself and calls, in order, `MUI_GUIINIT_OUTERDIALOG` (sets the header bitmap via
    `SetBrandingImage /IMGID=1046` — control ID 1046 confirmed from that same file — always with the light
    file, since the header never swaps), then `MUI_PAGE_FUNCTION_GUIINIT` (the Welcome page's own init, which
    extracts its bitmap to the fixed path `$PLUGINSDIR\modern-wizard.bmp` — confirmed from `Pages\Welcome.nsh`),
    and only THEN `MUI_CUSTOMFUNCTION_GUIINIT`. So by the time `WgSharpApplyDarkMode` runs, MUI has already
    extracted the light welcome bitmap to that fixed path; re-extracting (`File /oname=$PLUGINSDIR\modern-wizard.bmp ...`)
    over it with the dark file is enough, since the Welcome page's own nsDialogs control doesn't read that file
    until the page is actually shown, which happens after `.onGUIInit` fully completes. (An earlier revision
    also re-called `SetBrandingImage` to swap the HEADER to a dark variant too — removed per explicit direction
    that the header should always stay white; the header bitmap is a separate, non-overlapping control from
    the page title text, confirmed by cropping a real screenshot, so there was never a text-collision reason
    to have kept it dark in the first place.)
  - **Verified live** on this machine, which happened to already be in dark mode: full-window `PrintWindow`
    captures (not `CopyFromScreen` — an unrelated screen-capture quirk in this environment made full-desktop
    screenshots of the installer window intermittently blank while `PrintWindow` against the window handle
    directly worked reliably) of the Welcome page (dark title bar, dark navy welcome banner, red/orange
    wordmark) and the License page (header banner correctly stayed WHITE with the icon+wordmark, title bar
    still dark) of the real built installer, confirming native controls (license text box, buttons) correctly
    stayed light/unthemed as designed. Then flipped `AppsUseLightTheme` to light, re-ran the same installer
    (no rebuild needed — theme is read at runtime, not compile time), confirmed it fell back to the original
    all-light appearance with zero regression — then flipped the setting back to this machine's original value
    (dark) afterward.
- **Code signing**: `build.cmd`'s existing `:sign_file`/`:sign_locate` (SMCE certificate, `SIGN_CERT` env var
  override) now signs both `WgSharp.exe` (before packaging, so the installer embeds the signed exe) and the
  built `WgSharp-Setup.exe` (after `makensis` runs, since re-running the compiler after signing would
  invalidate the signature). **Found and fixed a real false-negative in `:sign_locate`'s own cert-presence
  check** while verifying this: `Get-ChildItem Cert:\CurrentUser\My` reported zero certificates when run from
  a `cmd.exe`-spawned `powershell.exe` subprocess (`HKCU\...\SystemCertificates\My\Certificates` didn't even
  exist from that process's view of the registry), on a machine where the SMCE cert genuinely is installed
  and `signtool.exe` — and `certutil -user -store My` — both find and use it successfully in that *exact same*
  process context. Root cause not fully chased down (looks like a PowerShell `Cert:` PSDrive quirk specific to
  a freshly-spawned non-interactive subprocess, not a real "cert missing" state), so `:sign_locate` now falls
  back to a `certutil -user -store My` / `certutil -store My` substring probe (mirroring signtool's own `/n`
  substring-match semantics) when the PowerShell check comes up empty, before concluding the cert truly isn't
  there.
- **Update checker asset** (`UpdateChecker.cs`): `Check()` now also extracts `InstallerAssetUrl` — the
  `browser_download_url` for an asset literally named `WgSharp-Setup.exe` (`InstallerAssetName`) — out of
  the same `releases/latest` JSON it already parses for the tag, via the same dependency-free regex-scraping
  approach `ExtractTagName` uses (bounded non-greedy match from `"name"` to `"browser_download_url"` so it
  can't spill past the current asset's own `{...}` object into a neighboring one — verified against a
  synthetic multi-asset JSON payload with the target asset listed *after* another one, compiled and run
  standalone). When present, `AboutDialog.ShowUpdateResult` / `MainForm.OnUpdateBalloonClicked` download it to
  `%TEMP%` and launch it SILENTLY (`UpdateChecker.LaunchInstallerSilently`, `/S` — no wizard, straight
  install-and-relaunch) instead of just opening the release page and making the user download and run it
  themselves. `ProcessStartInfo.UseShellExecute = true` is required, not just the default: Setup's manifest
  requests `requireAdministrator` (confirmed by scanning the compiled `WgSharp-Setup.exe`'s own manifest
  bytes, not assumed), and ShellExecute is what makes Windows honor that and show the UAC prompt for the
  CHILD process regardless of this (asInvoker) process's own elevation level. WgSharp exits right after
  launching — Setup's own `CloseRunningWgSharp` would handle a still-running WgSharp anyway, but exiting
  proactively here is the smoother path for a user-initiated update. Falls back to the old "open the release
  page" behavior on any download/launch failure, or when a release has no such asset (e.g. one published
  before this installer existed).
- **Verified live on this machine**: this machine had a genuine pre-existing MSI-based WgSharp 1.26.0705
  install (with two real DPAPI-encrypted tunnel configs) going into this work, making it a real upgrade-path
  test rather than a synthetic one. Ran the built, signed `WgSharp-Setup.exe /S` silently multiple times in a
  row — confirmed: the legacy MSI's Add/Remove Programs entry is detected and removed
  (`msiexec /x {80DF833D-...}`); a running GUI (interactive session) *and* a running `WgSharpSvc` (Session 0)
  are both correctly closed/stopped without a force-kill needed; files are replaced in place; the service
  ends up registered with the exact expected `binPath`, `start= auto`, `LocalSystem`; Start Menu shortcuts
  (`WgSharp.lnk` + `Uninstall WgSharp.lnk`) and the Add/Remove Programs entry are created correctly; the app
  auto-launches post-install and responds; both `WgSharp.exe` and `WgSharp-Setup.exe`'s Authenticode
  signatures verify as `Valid` (`CN=SMCE`). Then ran the silent uninstaller: confirmed complete removal
  (files, service registration, Start Menu folder, registry entry) while `ProgramData\WgSharp\conf`'s two
  tunnel configs and their exact byte sizes were untouched throughout every install/update/uninstall pass —
  then reinstalled to leave the machine in its original (installed, working) state.

## Service log pump (ServiceLogPump.cs, MainForm.cs, RemoteTunnelBackend.cs)
The GUI has always been able to pull the background service's in-memory log ring over the pipe (the LOG
command, `ServiceClient.FetchServiceLog` — see the Installer section above for the service side: an
always-on ring buffer, always populated, only written to `service.log` on disk when Debug log is on) and
show it in its own Log tab. The pump that actually does that pulling used to be gated entirely wrong: it
lived as an instance method (`RemoteTunnelBackend.PumpServiceLog`) only ever called while `MainForm._tunnel`
was a service-driven tunnel — at startup (`CheckForRunningServiceTunnel`, but only once it had already found
an *active* tunnel to report), after a successful activation, and on the once-a-second status tick, again
only while `_tunnel != null`. That meant any span where the service was running but nothing was active —
its own startup/driver-bootstrap chatter, or (the case that actually prompted this fix) a boot-time
auto-reconnect that *failed* — never reached the GUI's Log tab at all, regardless of whether the GUI was
freshly launched, restored from the tray, or just sitting open: there was no active tunnel object to hang
the pump off, so the pump code path was never reached, and the user had no way to see what the service had
actually done.
- **Fix**: pulled the pump out into `ServiceLogPump` (`src/core/ServiceLogPump.cs`), a static class with its
  own `LogLine` event and its own dedup state (`_lastLine`, matched by exact string — the service stamps every
  line with a millisecond timestamp, so content-based dedup is reliable even when several lines share a
  second), independent of any particular `RemoteTunnelBackend` instance. `MainForm` subscribes to
  `ServiceLogPump.LogLine += LogRaw` exactly once, in the constructor, instead of re-subscribing per tunnel
  instance. The whole pump body runs under one `lock` (covering the pipe round trip itself, not just the
  dedup bookkeeping) so the standing timer and an eager post-activation pump can't race and either drop or
  duplicate lines.
- **`statusTimer` now runs for the life of the form, not just while a tunnel is active**: started once,
  unconditionally (non-portable), from `RunDeferredStartupWork`, and no longer stopped in `DeactivateTunnel`.
  `OnStatusTick` calls `ServiceLogPump.Pump()` unconditionally before its (unchanged) `if (_tunnel == null)
  return;` guard, so the log keeps flowing in on the standing 1s tick regardless of whether anything is
  active. `CheckForRunningServiceTunnel` (startup detection) now pumps immediately after confirming the
  service is reachable, *before* checking STATUS for an active tunnel — so the very first thing the GUI does
  on launch is show whatever the service has already logged, active tunnel or not.
- **Verified live**: built and installed via the real signed `WgSharp-Setup.exe /S` (this machine's genuine
  install, with a real tunnel `NewValhalla`). Stopped the GUI, sent a raw `DEACTIVATE` over the control pipe
  so the service was genuinely idle (not just "no GUI watching"), confirmed via a direct `LOG` pipe query
  that the ring held the full history — boot-time driver bootstrap, the original reconnect, and the
  deactivation — then launched a fresh, unelevated GUI instance and read its Log tab (via `PrintWindow`,
  since the control isn't rendered as a real window until its tab has been visited at least once —
  `WM_GETTEXT` against the freshly-created but not-yet-composited control briefly read back empty, which is
  what motivated using a screenshot instead of trusting the raw text read here). The screenshot showed the
  complete service history from boot through the idle deactivation, entirely on its own with no tunnel
  active — confirming the fix. Reactivated `NewValhalla` afterward (confirmed `ACTIVE|...|Connected` with
  live rx/tx byte counts) to leave the machine back in its original working state.

## GUI responsiveness (MainForm.OnStatusTick, ServiceLogPump.cs, LOG2)
Symptoms were a stuttering UI (worst while dragging the window), and idle CPU on low-end machines.
- **Root cause: blocking named-pipe I/O on the UI thread, once a second.** `statusTimer` ticked on the UI
  thread and ran `ServiceLogPump.Pump()` (LOG) plus `RemoteTunnelBackend.GetStatus()` (STATUS) inline. Each is
  a connect/write/read round trip with a 2.5s connect timeout (and `GetStatus` retries up to 4x with sleeps
  on the first call). WM_TIMER is still dispatched inside the modal move/size loop, so every tick froze the
  drag for as long as the round trips took; if the service was stopped it froze for the full timeout each
  second. Fix: `OnStatusTick` queues the I/O to the thread pool (`_pollBusy` guard, a tick that finds the
  previous poll still running skips) and `ApplyStatus` runs the cheap result application back on the UI thread.
  `ServiceLogPump.LogLine` therefore now fires on a pool thread; `LogRaw` already marshals.
- **LOG2 (incremental log fetch).** The old LOG command shipped the whole 200-line ring every second even
  when idle, then escaped/unescaped/split/deduped it. `LOG2|<epoch>|<since>` returns only lines added since
  the cursor (`LOG2|<epoch>|<total>|<payload>`); the epoch is a per-service-process GUID so a service restart
  resets the cursor. An older service answers `ERROR:unsupported`; the pump then falls back to LOG + dedup
  permanently (`_legacy`). When the service is unreachable the pump backs off 5s (`ResetBackoff()` on
  activation) instead of retrying every tick.
- **Smaller items**: the tunnel list was invalidated twice per tick though the shield only depends on
  `_active` (removed; activate/deactivate already invalidate); `AreaChart` allocated 3 fonts per paint and
  invalidated while its tab was hidden; `StatsPanel` updated hidden labels; `StatusRow.Set` invalidated even
  when unchanged; `ThemedGroupBox.LabelAreaHeight` ran `TextRenderer.MeasureText` on every layout query;
  `BuildDetail` recomputed the X25519 public key on every call and, via `Controls.Clear()`, leaked the old
  Label/TableLayoutPanel handles on every rebuild (now disposed, wrapped in one Suspend/ResumeLayout).
- **Verified**: compiles clean with the build.cmd csc flags. NOT yet exercised at runtime against a live
  service (LOG2 round trip, legacy-service fallback, drag smoothness); do that after installing.

## CLI, diagnostics, auto-connect, tray status icon
- **CLI** (`src/core/Cli.cs`, dispatched from `Program.Main` before the single-instance mutex): `--up/--down/--status/--list`
  over the existing pipe (ACTIVATE/DEACTIVATE/STATUS); no new service commands. Output uses `AttachConsole(-1)`.
  Not exercised against a live activation yet; `--list`/`--status` exit codes were checked.
- **Diagnostics** (`src/core/ConnectionDiagnostics.cs`): state machine fed by `ApplyStatus`. Flags "handshaking >10s, no
  reply" and "tx growing, rx flat for 20s". A stale handshake alone is NOT flagged (idle tunnels legitimately never
  rekey). The **Test** button (built per `BuildDetail`) pings the first literal IP in the config's `DNS`.
- **Auto-connect** (`src/core/NetworkRules.cs`, `MainForm.ApplyNetworkRule`): GUI-side, so it needs the window/tray process
  running (service-side was deliberately not done). SSIDs via wlanapi.dll (struct offsets taken from the Win32 docs, not
  yet verified on a machine with Wi-Fi). Settings: `AutoConnectEnabled/Tunnel`, `TrustedNetworks`, `WiredIsTrusted`;
  tunnel picked via the tunnel list context menu. One attempt per network key; a manual disconnect sets
  `_ruleSuppressed` until the network key changes. Disabled in portable mode.
- **Tray icon states**: `UpdateTrayIcon` shows `WgSharp-grey.ico` when idle/failed (set in the constructor so an idle
  launch starts grey), flashes grey/regular every 500ms while negotiating, and shows the regular icon when connected.
  The grey icon is a pre-made multi-frame .ico (each PNG frame of `WgSharp.ico` converted to luma greyscale, alpha
  kept), embedded by `build.cmd` as the managed resource `WgSharp.TrayGrey.ico` and loaded with `new Icon(stream)`.
  Do NOT render tray variants at runtime via `new Icon(icon, size)`/`ToBitmap()`: with PNG-compressed frames that
  path hits the GDI+ bug from `AppIconLoader` and produced visibly corrupted icons. Regenerate the grey .ico if
  `WgSharp.ico` changes.
