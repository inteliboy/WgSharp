; ============================================================================
; WgSharp NSIS installer.
;
; Replaces the old WiX/MSI installer (installer\Product.wxs, removed) -
; MSI's own upgrade/versioning model kept tripping over this project's
; date-based same-day version stamps (see that file's old MajorUpgrade
; comment for the history), and NSIS's own scripted flow makes "close the
; running app the right way, register the service, replace the files, clean
; up any leftover MSI install" a lot more direct to express and debug than
; WiX's declarative component/table model was.
;
; Built directly with makensis.exe from build.cmd - no separate installer
; project file, consistent with how the rest of WgSharp is built (csc.exe
; only, no MSBuild).
;
; Inputs passed in from build.cmd via /D command-line defines:
;   VERSION            e.g. "1.26.0908" - the leading 3 fields of the exe's
;                       own 4-field AssemblyVersion (see build.cmd); shown as
;                       the installer's DisplayVersion in Add/Remove Programs.
;   VERSION_ASSEMBLY    e.g. "1.26.0908.0" - used for the installer exe's own
;                       Win32 version resource (VIProductVersion requires
;                       exactly 4 numeric fields).
;   SRCDIR              full path to the built bin\amd64 folder (contains the
;                       already-signed WgSharp.exe; see build.cmd's call to
;                       :sign_file, which runs BEFORE this script).
;   REPODIR             full path to the repo root (contains LICENSE,
;                       README.md, WgSharp.ico).
;
; What this installs, matching the old MSI's own behavior:
;   - WgSharp.exe to a FIXED location, $PROGRAMFILES64\WgSharp (no "choose a
;     folder" page - see the page-selection comment below for why).
;   - LICENSE.txt and README.md alongside it.
;   - A Start Menu shortcut, plus an Uninstall shortcut next to it.
;   - Standard Add/Remove Programs registration.
;   - Registers the WgSharpSvc background service (LocalSystem, auto-start)
;     at install time, so the unelevated GUI never has to prompt for
;     elevation just to set it up (same reasoning as the old MSI: the
;     installer is already elevated, so doing it here is free). Only
;     registered, not started - it idles until a tunnel is activated.
;   - Closes a running WgSharp automatically before touching its files, both
;     the GUI and the WgSharpSvc service - see CloseWgSharp.ps1.
;   - Detects and silently removes any leftover MSI-based WgSharp install
;     (see RemoveLegacyMsiInstalls) before laying down files, so upgrading
;     from an old MSI install to this installer "just works" the same way a
;     same-installer update does.
;   - Follows the OS dark/light theme: dark title bar plus a dark-variant
;     welcome/finish banner when Windows is set to dark (see
;     WgSharpApplyDarkMode below). The header banner (next to the page
;     title) stays white in both themes - it's an unrelated control to the
;     title text, not a light/dark consistency requirement. Covers the
;     window chrome and that one bitmap, not every native control (License
;     page, buttons, progress bar stay the standard system light controls) -
;     see that function's own comment for why a full control repaint wasn't
;     attempted.
;
; What this deliberately does NOT do: start the service, touch the Windows
; Firewall, or enable login autostart, or touch ProgramData\WgSharp\conf or
; the HKLM\Software\WgSharp settings key. Firewall pre-authorization and
; autostart remain WgSharp's OWN first-run behavior (see
; src\core\InstallLocation.cs) when it detects it's running from this exact
; installed path. Tunnel configs and settings are never part of what this
; installer (or its uninstaller) manages - see ConfigStore.cs / AppSettings.cs -
; so install, update, AND uninstall all leave a user's saved tunnels and
; settings completely untouched.
; ============================================================================

Unicode true

!include "MUI2.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!insertmacro GetSize

!ifndef VERSION
  !define VERSION "0.0.0"
!endif
!ifndef VERSION_ASSEMBLY
  !define VERSION_ASSEMBLY "0.0.0.0"
!endif
!ifndef SRCDIR
  !error "SRCDIR must be defined (pass /DSRCDIR=<path to bin\amd64>)"
!endif
!ifndef REPODIR
  !error "REPODIR must be defined (pass /DREPODIR=<repo root>)"
!endif

!define PRODUCT_NAME "WgSharp"
!define SERVICE_NAME "WgSharpSvc"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\WgSharp"

; Set once in .onInit/un.onInit (before any window exists), consumed in
; WgSharpApplyDarkMode/un.WgSharpApplyDarkMode (once the main window does).
Var IsDarkMode

Name "${PRODUCT_NAME}"
OutFile "${SRCDIR}\WgSharp-Setup.exe"
InstallDir "$PROGRAMFILES64\WgSharp"
InstallDirRegKey HKLM "Software\WgSharp" "InstallDir"
RequestExecutionLevel admin
SetCompressor /SOLID lzma

VIProductVersion "${VERSION_ASSEMBLY}"
VIAddVersionKey "ProductName" "WgSharp"
VIAddVersionKey "CompanyName" "inteliboy"
VIAddVersionKey "FileDescription" "WgSharp Setup"
VIAddVersionKey "FileVersion" "${VERSION_ASSEMBLY}"
VIAddVersionKey "ProductVersion" "${VERSION_ASSEMBLY}"
VIAddVersionKey "LegalCopyright" "Copyright (c) 2026 inteliboy"

; ---- UI: installer/uninstaller icon + wizard banners, all derived from the
; app's own WgSharp.ico (see installer\banner-header.bmp / banner-welcome.bmp,
; generated once from the same 256x256 embedded PNG frame AppIconLoader.cs
; reads out of the exe at runtime - regenerate them if WgSharp.ico changes). ----
!define MUI_ICON "${REPODIR}\WgSharp.ico"
!define MUI_UNICON "${REPODIR}\WgSharp.ico"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_BITMAP "${REPODIR}\installer\banner-header.bmp"
!define MUI_HEADERIMAGE_UNBITMAP "${REPODIR}\installer\banner-header.bmp"
!define MUI_WELCOMEFINISHPAGE_BITMAP "${REPODIR}\installer\banner-welcome.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "${REPODIR}\installer\banner-welcome.bmp"
!define MUI_ABORTWARNING

; No "choose install folder" page: WgSharp detects when it's running from
; the fixed $PROGRAMFILES64\WgSharp path (InstallLocation.cs) and changes its
; defaults accordingly (auto-enables autostart + the background service,
; disallows Portable mode) - a movable install location would break that
; detection, same reasoning the old MSI's own comment gave. No finish page
; either: SetAutoClose (in each section, below) closes the wizard the moment
; work completes, and WgSharp is launched directly from the install section -
; matching the old MSI's ProgressDlg-closes-itself / LaunchApplication
; behavior instead of requiring one more click through a Finish screen.
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${REPODIR}\LICENSE"
!insertmacro MUI_PAGE_INSTFILES

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

; Runs once the main window exists (MUI2's own .onGUIInit/un.onGUIInit,
; generated by !insertmacro MUI_LANGUAGE below), right after MUI's own
; outer-dialog and Welcome-page setup - see WgSharpApplyDarkMode's own
; comment for exactly why that ordering matters.
!define MUI_CUSTOMFUNCTION_GUIINIT WgSharpApplyDarkMode
!define MUI_CUSTOMFUNCTION_UNGUIINIT un.WgSharpApplyDarkMode

!insertmacro MUI_LANGUAGE "English"

; ============================================================================
; .onInit / un.onInit - architecture guard + 64-bit registry view.
; ============================================================================
Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "WgSharp requires a 64-bit x64 (amd64) processor. This machine's architecture is not supported."
    Quit
  ${EndIf}
  ; Wintun/WireGuardNT install a kernel-mode driver, and Windows' CPU
  ; emulation only covers user-mode code, so an x64 build running under
  ; emulation (e.g. ARM64) would never reach a working driver - same
  ; Msix64 condition the old MSI enforced. ${RunningX64} reports the true
  ; underlying hardware architecture, not just this (32-bit) installer
  ; process's own bitness, so it also correctly blocks ARM64.
  SetRegView 64

  ; Same registry value src/ui/AppTheme.cs's IsDark static initializer reads
  ; (HKCU, so it follows the CURRENT USER's preference, matching the app's
  ; own behavior - the installer runs elevated but this key is never
  ; virtualized/redirected by that). ReadRegDWORD leaves $0 empty when the
  ; value doesn't exist (pre-Windows 10 1809), which correctly falls through
  ; to the IsDarkMode=0 (light) default below rather than matching "0".
  StrCpy $IsDarkMode 0
  ReadRegDWORD $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Themes\Personalize" "AppsUseLightTheme"
  ${If} $0 == 0
    StrCpy $IsDarkMode 1
  ${EndIf}
FunctionEnd

Function un.onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "WgSharp requires a 64-bit x64 (amd64) processor. This machine's architecture is not supported."
    Quit
  ${EndIf}
  SetRegView 64

  StrCpy $IsDarkMode 0
  ReadRegDWORD $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Themes\Personalize" "AppsUseLightTheme"
  ${If} $0 == 0
    StrCpy $IsDarkMode 1
  ${EndIf}
FunctionEnd

; ============================================================================
; Dark/light theme: called once per process from MUI_CUSTOMFUNCTION_GUIINIT /
; _UNGUIINIT (see the !define above), i.e. once the main window exists but
; before any page has been shown yet.
;
; What this covers: the window's own title bar/frame (DwmSetWindowAttribute
; DWMWA_USE_IMMERSIVE_DARK_MODE) and the welcome/finish sidebar bitmap - the
; exact same uxtheme.dll/dwmapi.dll calls src\ui\NativeMethods.cs uses for
; the app's own window, ported here via System::Call since NSIS has no
; managed P/Invoke layer to reuse directly.
;
; The HEADER bitmap (the small one next to the page title) deliberately
; stays white in both themes - it's a separate, fixed-width control (ID
; 1046) from the page title/subtitle text, which lives in its own
; unrelated white background panel further right (confirmed by cropping a
; real screenshot pixel-by-pixel, not assumed - there's no overlap risk
; either way), so there's no requirement to darken it for consistency with
; anything else on that strip.
;
; What this deliberately does NOT cover: the License page's text box, the
; buttons, the progress bar, and the rest of MUI2's native Win32 controls
; stay the standard system (light) appearance even in dark mode. Properly
; dark-theming every one of those needs per-control subclassing (WM_CTLCOLOR*
; interception, owner-drawn buttons, etc.) that's normally done via a
; dedicated compiled NSIS plugin (e.g. the community "NsisDarkMode" plugin) -
; a much larger, more fragile undertaking than a wizard installer warrants,
; and not attempted here. Title bar + welcome banner gets the installer
; visually out of the jarring "pure white window" look in an otherwise dark
; desktop without that risk.
;
; Ordering this relies on: MUI_FUNCTION_GUIINIT (Contrib\Modern UI 2\
; Interface.nsh) calls MUI_PAGE_FUNCTION_GUIINIT (the Welcome page's own
; GUIInit, which extracts its bitmap to the fixed path
; $PLUGINSDIR\modern-wizard.bmp) BEFORE calling MUI_CUSTOMFUNCTION_GUIINIT -
; verified directly from the installed NSIS's own source on this machine,
; not assumed. So by the time this function runs, MUI has already extracted
; the light welcome bitmap to that fixed path; re-extracting here with the
; dark file overrides it before the Welcome page - whose own nsDialogs
; bitmap control reads that file lazily at page-show time, not at extract
; time - is ever actually shown.
Function WgSharpApplyDarkMode
  ${If} $IsDarkMode == 1
    System::Call 'uxtheme::#135(i 1)' ; SetPreferredAppMode(dark) - process-wide, harmless if it no-ops on older Windows
    System::Call 'uxtheme::#136()'    ; FlushMenuThemes
    System::Call 'dwmapi::DwmSetWindowAttribute(i $HWNDPARENT, i 20, *i 1, i 4) i.r0'
    ${If} $0 != 0
      ; Attribute 20 is Windows 10 20H1+/11; 19 is the earlier 1809-era value
      ; (see NativeMethods.cs's own SetDarkTitleBar comment) - try that next.
      System::Call 'dwmapi::DwmSetWindowAttribute(i $HWNDPARENT, i 19, *i 1, i 4)'
    ${EndIf}

    File "/oname=$PLUGINSDIR\modern-wizard.bmp" "banner-welcome-dark.bmp"
  ${EndIf}
FunctionEnd

; No Welcome/Finish page on the uninstaller (Confirm + InstFiles only, and
; the header bitmap stays white regardless - see above), so only the title
; bar applies here.
Function un.WgSharpApplyDarkMode
  ${If} $IsDarkMode == 1
    System::Call 'uxtheme::#135(i 1)'
    System::Call 'uxtheme::#136()'
    System::Call 'dwmapi::DwmSetWindowAttribute(i $HWNDPARENT, i 20, *i 1, i 4) i.r0'
    ${If} $0 != 0
      System::Call 'dwmapi::DwmSetWindowAttribute(i $HWNDPARENT, i 19, *i 1, i 4)'
    ${EndIf}
  ${EndIf}
FunctionEnd

; ============================================================================
; Shared install-time helpers.
; ============================================================================

; Detects any leftover MSI-based WgSharp install (Add/Remove Programs entries
; keyed by a ProductCode GUID rather than a plain name; the old Product.wxs's
; UpgradeCode meant a new ProductCode was minted on every build, so there's
; no single GUID to hardcode here) and removes it silently via msiexec before
; this installer's own files are laid down. This never touches
; ProgramData\WgSharp\conf or the HKLM\Software\WgSharp settings key - the
; old MSI's own component set never covered either of those (see the header
; comment above), so msiexec /x is exactly as safe here as it always was.
Function RemoveLegacyMsiInstalls
  Push $0 ; EnumRegKey index
  Push $1 ; subkey name (a MSI ProductCode GUID, when it matches)
  Push $2 ; DisplayName
  Push $3 ; first character of $1
  Push $4 ; msiexec exit code

  StrCpy $0 0
  rlm_loop:
    EnumRegKey $1 HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall" $0
    StrCmp $1 "" rlm_done
    IntOp $0 $0 + 1
    StrCpy $3 $1 1
    StrCmp $3 "{" 0 rlm_loop ; only brace-GUID keys are MSI ProductCodes; our own uninstall key is named plainly "WgSharp"
    ReadRegStr $2 HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\$1" "DisplayName"
    StrCmp $2 "WgSharp" 0 rlm_loop
    DetailPrint "Removing previous MSI-based WgSharp installation ($1)..."
    ExecWait '"$SYSDIR\msiexec.exe" /x $1 /qn /norestart' $4
    DetailPrint "msiexec /x $1 exited with code $4"
    ; That subkey just vanished from under the enumeration, shifting every
    ; later index down by one - restart the scan from the top rather than
    ; risk silently skipping whatever shifted into $0's old slot. There are
    ; only ever a handful of Uninstall entries, so a second full pass costs
    ; nothing, and this converges once no more MSI-based "WgSharp" entries
    ; remain.
    StrCpy $0 0
    Goto rlm_loop
  rlm_done:

  Pop $4
  Pop $3
  Pop $2
  Pop $1
  Pop $0
FunctionEnd

; Closes a running WgSharp GUI and stops WgSharpSvc via CloseWgSharp.ps1 (see
; that file for why a plain WM_CLOSE/taskkill doesn't actually work here).
; Bundled into the installer/uninstaller via File and extracted to
; $PLUGINSDIR (NSIS's own auto-cleaned temp dir) rather than left in
; $INSTDIR, so nothing extra is ever left behind on disk.
Function CloseRunningWgSharp
  InitPluginsDir
  File "/oname=$PLUGINSDIR\CloseWgSharp.ps1" "CloseWgSharp.ps1"
  DetailPrint "Closing any running WgSharp instance and stopping the WgSharpSvc service..."
  nsExec::ExecToLog 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\CloseWgSharp.ps1" -TimeoutSeconds 15 -InstallDir "$INSTDIR"'
  Pop $0
FunctionEnd

; Registers (or re-points) the WgSharpSvc service, matching
; ServiceInstaller.Install()'s own logic exactly: create it if missing,
; else reconfigure the existing registration; LocalSystem; start=auto (an
; idle manager - it doesn't activate a tunnel just by being started at
; boot). Not started here, same as the old MSI: it idles until a tunnel is
; activated, or reconnects one at boot from its own persisted state.
Function InstallService
  nsExec::ExecToLog 'sc.exe query ${SERVICE_NAME}'
  Pop $0
  ${If} $0 == "0"
    DetailPrint "Reconfiguring the ${SERVICE_NAME} service..."
    nsExec::ExecToLog 'sc.exe config ${SERVICE_NAME} binPath= "\"$INSTDIR\WgSharp.exe\" --service" start= auto'
    Pop $0
  ${Else}
    DetailPrint "Registering the ${SERVICE_NAME} service..."
    nsExec::ExecToLog 'sc.exe create ${SERVICE_NAME} binPath= "\"$INSTDIR\WgSharp.exe\" --service" start= auto obj= LocalSystem DisplayName= "WgSharp Tunnel Service"'
    Pop $0
    nsExec::ExecToLog 'sc.exe description ${SERVICE_NAME} "Runs WgSharp WireGuard tunnels and reconnects them before login."'
    Pop $0
  ${EndIf}
FunctionEnd

; ============================================================================
; Install
; ============================================================================
Section "-Main" SecMain
  SetShellVarContext all

  Call RemoveLegacyMsiInstalls
  Call CloseRunningWgSharp

  SetOutPath "$INSTDIR"
  SetOverwrite on
  File "${SRCDIR}\WgSharp.exe"
  File "/oname=LICENSE.txt" "${REPODIR}\LICENSE"
  File "${REPODIR}\README.md"

  Call InstallService

  WriteRegStr HKLM "Software\WgSharp" "InstallDir" "$INSTDIR"

  CreateDirectory "$SMPROGRAMS\WgSharp"
  CreateShortcut "$SMPROGRAMS\WgSharp\WgSharp.lnk" "$INSTDIR\WgSharp.exe" "" "$INSTDIR\WgSharp.exe" 0
  CreateShortcut "$SMPROGRAMS\WgSharp\Uninstall WgSharp.lnk" "$INSTDIR\Uninstall.exe"

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayName" "WgSharp"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINST_KEY}" "Publisher" "inteliboy"
  WriteRegStr HKLM "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\WgSharp.exe,0"
  WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKLM "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegStr HKLM "${UNINST_KEY}" "HelpLink" "https://github.com/inteliboy/WgSharp"
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "EstimatedSize" "$0"

  ; Launch WgSharp once Setup finishes, same as the old MSI's asyncNoWait
  ; LaunchApplication custom action - fire-and-forget, the wizard doesn't
  ; wait for WgSharp to exit before closing.
  Exec '"$INSTDIR\WgSharp.exe"'

  SetAutoClose true
SectionEnd

; ============================================================================
; Uninstall
; ============================================================================
Function un.CloseRunningWgSharp
  InitPluginsDir
  File "/oname=$PLUGINSDIR\CloseWgSharp.ps1" "CloseWgSharp.ps1"
  DetailPrint "Closing any running WgSharp instance and stopping the WgSharpSvc service..."
  nsExec::ExecToLog 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\CloseWgSharp.ps1" -TimeoutSeconds 15 -InstallDir "$INSTDIR"'
  Pop $0
FunctionEnd

Function un.UninstallService
  DetailPrint "Removing the ${SERVICE_NAME} service..."
  nsExec::ExecToLog 'sc.exe stop ${SERVICE_NAME}'
  Pop $0
  nsExec::ExecToLog 'sc.exe delete ${SERVICE_NAME}'
  Pop $0
FunctionEnd

Section "Uninstall"
  SetShellVarContext all

  Call un.CloseRunningWgSharp
  Call un.UninstallService

  ; wintun.dll / wireguard.dll are downloaded by WgSharp itself at first
  ; run/connect (see WintunDownloader.cs / WireGuardNtDownloader.cs)
  ; straight into $INSTDIR, so this installer never tracked them as its own
  ; files - remove them explicitly here anyway so a normal uninstall doesn't
  ; leave them (or $INSTDIR itself) behind, same as the old MSI's own
  ; explicit RemoveFile entries for both.
  Delete "$INSTDIR\WgSharp.exe"
  Delete "$INSTDIR\LICENSE.txt"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\wintun.dll"
  Delete "$INSTDIR\wireguard.dll"
  Delete "$INSTDIR\wgsharp-crash.txt"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"

  Delete "$SMPROGRAMS\WgSharp\WgSharp.lnk"
  Delete "$SMPROGRAMS\WgSharp\Uninstall WgSharp.lnk"
  RMDir "$SMPROGRAMS\WgSharp"

  DeleteRegKey HKLM "${UNINST_KEY}"
  DeleteRegValue HKLM "Software\WgSharp" "InstallDir"
  ; Deliberately does NOT delete the "Software\WgSharp" key itself, nor
  ; ProgramData\WgSharp\conf - those hold AppSettings (AppSettings.cs) and
  ; tunnel configs (ConfigStore.cs) respectively. Uninstalling WgSharp
  ; should not throw away a user's saved tunnels or settings; reinstalling
  ; later picks both straight back up.

  SetAutoClose true
SectionEnd
