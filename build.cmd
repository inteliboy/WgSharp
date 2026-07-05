@echo off
rem ============================================================
rem  WgSharp build script -- .NET Framework 4.8, csc.exe only.
rem  No MSBuild, no .csproj. Builds amd64 (x64) only, to bin\amd64.
rem
rem  WgSharp now targets x64 exclusively. The app refuses to start
rem  on any other architecture (see Program.cs), so there's no
rem  reason to emit an x86 or arm64 binary:
rem    - x86: the project is x64-only now, both at build and run time.
rem    - arm64: the legacy csc.exe used here only accepts /platform of
rem      anycpu, x86, x64, arm, anycpu32bitpreferred, or Itanium -- it
rem      predates ARM64 Windows; and .NET Framework 4.8 has no ARM64
rem      desktop CLR anyway, and Wintun/WireGuardNT install a kernel
rem      driver that Windows' CPU emulation never covers. ARM64 machines
rem      should run the amd64 build under x64 emulation for user-mode
rem      code (the kernel driver still won't load, which WgSharp detects
rem      and refuses at startup rather than failing confusingly later).
rem
rem  One exe does double duty as both the GUI and the optional
rem  background service: Program.cs's Main() checks for a "--service"
rem  argument (set by ServiceInstaller in the service's binPath) and
rem  runs ServiceBase.Run(...) instead of the GUI in that case. No
rem  separate WgSharpSvc.exe.
rem
rem  NOTE: deliberately does NOT use `setlocal enabledelayedexpansion`
rem  or build a response file by appending each path. Both break when
rem  the install path contains parentheses (e.g. "WgSharp (18)") or
rem  other special characters, which silently yields a tiny, empty exe.
rem  Instead we cd into the project dir and pass a relative wildcard,
rem  which csc expands itself -- robust against spaces and parens.
rem ============================================================

setlocal

rem --- Work from the script's own directory -------------------
pushd "%~dp0"

rem --- Locate csc.exe from the .NET Framework 4.x install -----
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] Could not find csc.exe under %WINDIR%\Microsoft.NET.
    echo         Is the .NET Framework 4.x developer pack installed?
    popd
    exit /b 1
)
echo Using compiler: %CSC%

rem --- Reference assemblies (GAC-resolved by simple name) -----
rem  System.ServiceProcess.dll is needed for the service-mode branch
rem  (ServiceBase), even though most launches use the GUI branch.
set REFS=/r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Net.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Security.dll /r:System.ServiceProcess.dll

rem --- version stamp: 1.YY.MMDD (+ .0 on the exe), generated fresh every build ----
rem Each build's exe (and, if built, the MSI) carries today's date as its
rem version, so two builds are easy to tell apart and "what version is this"
rem always has an unambiguous answer (no separate version bump to remember).
rem %DATE%/%TIME% are locale-dependent (format varies by region/Windows
rem locale), so we get year/month/day from PowerShell's culture-invariant
rem Get-Date instead of parsing %DATE% -- robust regardless of the
rem machine's locale.
rem
rem MM and DD are zero-padded (2 digits each) and concatenated into a single
rem 4-digit build number -- e.g. 1.26.0629 for 2026-06-29. This is NOT for
rem cosmetic leading-zero appearance (Windows version fields are numeric, so
rem "0629" and "629" parse to the identical value 629 either way -- there's
rem no display difference to "fix"). Padding is required for CORRECTNESS:
rem without it, %MM%%DD% collides between dates -- January 23 (month=1,
rem day=23 -> "123") and December 3 (month=12, day=3 -> "123") would
rem produce the exact same version number. Padding to a fixed 2+2 digit
rem width makes every MMDD value unique. (An earlier revision of this
rem script used unpadded month/day for what seemed like consistency with
rem Explorer's numeric display, but that was solving a non-problem at the
rem cost of introducing this collision -- fixed here.)
rem
rem ONE date-encoding, used in two forms (MSI's ProductVersion format is
rem fundamentally 3-field only -- "major.minor.build" -- WiX rejects a 4th
rem field there outright, so a single identical string for both isn't
rem possible; this is as unified as Windows Installer allows):
rem   - VERSION       = 1.YY.MMDD     (3 fields) -- the MSI's ProductVersion
rem                     and the release tag / portable-zip name.
rem   - VERSION_ASSEMBLY = 1.YY.MMDD.REV (4 fields) -- the exe's AssemblyVersion/
rem                     AssemblyFileVersion/AssemblyInformationalVersion. REV
rem                     is 0 for the normal one-build-per-day case.
rem
rem Same-day RE-release is handled AUTOMATICALLY by a build timestamp stamped
rem into the exe (see BUILD_TS below): when the tag and the running build are
rem the same version, the update check compares the release asset's upload time
rem against that stamp (5-min margin), so a re-uploaded same-tag build is seen
rem as an update without any manual step. Setting REV (below) is an OPTIONAL
rem way to give a same-day re-release an explicit distinct version instead.
rem Only the exe's 4th field changes; VERSION (MSI ProductVersion + tag/zip
rem display) stays 1.YY.MMDD.
rem The same YY/MM/DD digits are what's actually meaningful, and VERSION is
rem exactly the leading 3 fields of VERSION_ASSEMBLY.
set MM=
set DD=
set YY=
for /f "tokens=1-3 delims=." %%a in ('powershell -NoProfile -NonInteractive -Command "(Get-Date).ToString('yy.MM.dd')"') do (
    set YY=%%a
    set MM=%%b
    set DD=%%c
)
if not defined YY (
    echo [WARN] Could not get the date from PowerShell; using a fallback version stamp.
    set MM=00
    set DD=00
    set YY=00
)

rem Precise UTC build timestamp (ISO-8601, e.g. 2026-07-05T14:03:22Z), stamped
rem into the exe so the in-app update check can use it as a TIEBREAKER when the
rem version compares equal (a same-day re-release without a REV/tag bump): the
rem GitHub asset's updated_at being meaningfully newer than this means a newer
rem build exists. Best-effort; empty on failure (the check then just skips the
rem tiebreaker and relies on version comparison alone).
set BUILD_TS=
for /f "usebackq delims=" %%t in (`powershell -NoProfile -NonInteractive -Command "[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')"`) do set BUILD_TS=%%t
set VERSION=1.%YY%.%MM%%DD%
rem Exe revision. The exe's 4th field is what the in-app update check reads and
rem compares against the GitHub release tag. Normally it's 0 (one build/day).
rem For a SECOND release on the same calendar day, set REV=1 (then 2, ...)
rem before running build.cmd AND tag that GitHub release 1.YY.MMDD.REV to match:
rem the installed app then reports 1.YY.MMDD.REV and won't false-prompt itself
rem as out of date against its own tag. Only the exe stamp changes; VERSION
rem (the MSI ProductVersion and the friendly/tag display) stays 1.YY.MMDD.
rem   Note: because the MSI ProductVersion is unchanged, re-running the same-day
rem   MSI over a prior same-day install is a repair, not an upgrade -- fine
rem   here, since only the running app checks for updates (it sends people to
rem   the download page; they install the new build fresh). The MSI installer
rem   itself never checks for updates.
if not defined REV set REV=0
set VERSION_ASSEMBLY=%VERSION%.%REV%
echo Version stamp: %VERSION_ASSEMBLY% ^(exe^) / %VERSION% ^(MSI/tag^)

> "src\core\AssemblyInfo.generated.cs" (
    echo // Auto-generated by build.cmd on every build -- do not edit by hand.
    echo // Stamps the exe with today's date as its version: 1.YY.MMDD.0.
    echo using System.Reflection;
    echo [assembly: AssemblyVersion^("%VERSION_ASSEMBLY%"^)]
    echo [assembly: AssemblyFileVersion^("%VERSION_ASSEMBLY%"^)]
    echo [assembly: AssemblyInformationalVersion^("%VERSION_ASSEMBLY%"^)]
    echo [assembly: AssemblyMetadata^("BuildTimestamp", "%BUILD_TS%"^)]
    echo [assembly: AssemblyProduct^("WgSharp"^)]
    echo [assembly: AssemblyTitle^("WgSharp"^)]
    echo [assembly: AssemblyDescription^("A from-scratch WireGuard client for Windows"^)]
    echo [assembly: AssemblyCopyright^("Copyright \u00A9 2026 inteliboy"^)]
)

rem --- amd64 (x64) -- the only build ------------------------------
if not exist "bin\amd64" mkdir "bin\amd64"
echo.
echo === Building amd64 -^> bin\amd64\WgSharp.exe ===
"%CSC%" /nologo /target:winexe /platform:x64 ^
    /langversion:5 ^
    /define:TRACE ^
    /out:"bin\amd64\WgSharp.exe" ^
    /win32manifest:"app.manifest" ^
    /win32icon:"WgSharp.ico" ^
    %REFS% ^
    /recurse:src\core\*.cs /recurse:src\crypto\*.cs /recurse:src\proto\*.cs /recurse:src\net\*.cs /recurse:src\tun\*.cs /recurse:src\ui\*.cs /recurse:src\svc\*.cs ^
    "src\Program.cs"
if %ERRORLEVEL% neq 0 (
    echo [BUILD FAILED] amd64 build returned %ERRORLEVEL%.
    popd
    exit /b %ERRORLEVEL%
)

rem --- Code signing: exe (must happen BEFORE the MSI is built, so the ---
rem --- MSI packs the SIGNED exe). Silently skipped when the SIGN_CERT ---
rem --- certificate isn't installed on this machine.                   ---
call :sign_file "bin\amd64\WgSharp.exe"

echo Sample config lives in README.md (no sample.conf is shipped/installed).

rem --- MSI installer (WiX Toolset v3.14) -----------------------------
rem Best-effort: a missing WiX install does NOT fail the build, since the
rem exe (the thing most people actually need) already built fine above. We
rem just skip the installer and say so clearly.
set "WIXBIN="
if defined WIX if exist "%WIX%bin\candle.exe" set "WIXBIN=%WIX%bin"
if not defined WIXBIN if exist "%ProgramFiles(x86)%\WiX Toolset v3.14\bin\candle.exe" set "WIXBIN=%ProgramFiles(x86)%\WiX Toolset v3.14\bin"
if not defined WIXBIN if exist "%ProgramFiles%\WiX Toolset v3.14\bin\candle.exe" set "WIXBIN=%ProgramFiles%\WiX Toolset v3.14\bin"

if not defined WIXBIN (
    echo.
    echo [SKIP] WiX Toolset v3.14 not found ^(checked %%WIX%% and the usual
    echo        Program Files install path^) -- skipping the MSI installer.
    echo        Install it from https://wixtoolset.org/ if you want one.
    goto :after_msi
)

echo.
echo === Building installer -^> bin\amd64\WgSharp-Setup.msi ===
if not exist "obj" mkdir "obj"
set "REPODIR=%CD%"
set "SRCDIR=%CD%\bin\amd64"

"%WIXBIN%\candle.exe" -nologo ^
    -ext WixUIExtension -ext WixUtilExtension ^
    -dProductVersion="%VERSION%" ^
    -dSourceDir="%SRCDIR%" -dRepoDir="%REPODIR%" ^
    -out "obj\Product.wixobj" ^
    "installer\Product.wxs"
if %ERRORLEVEL% neq 0 (
    echo [WARN] candle.exe failed ^(%ERRORLEVEL%^) -- MSI not built; the exe above is still fine.
    goto :after_msi
)

rem --- Ask whether to keep the .wixpdb installer symbol file ----------
rem  light.exe writes WgSharp-Setup.wixpdb next to the MSI by default. It's
rem  only useful for authoring binary MSP patches between two MSI versions
rem  (torch.exe) -- which this project doesn't do; it ships full MajorUpgrade
rem  MSIs -- so by default we suppress it with -spdb to keep bin\amd64 clean
rem  and avoid accidentally publishing the installer's internal structure.
rem  Override without being prompted by setting WIXPDB=1 (keep) or WIXPDB=0
rem  (suppress) before running; handy for CI, where a blocking prompt would
rem  hang. Interactive default (just pressing Enter) is No / suppress.
rem  Done via a CALL (not an inline if-block) so this stays correct without
rem  enabledelayedexpansion, which the header explains must not be used here.
call :decide_wixpdb

"%WIXBIN%\light.exe" -nologo ^
    -ext WixUIExtension -ext WixUtilExtension ^
    -sice:ICE61 ^
    %SPDB_FLAG% ^
    -out "bin\amd64\WgSharp-Setup.msi" ^
    "obj\Product.wixobj"
if %ERRORLEVEL% neq 0 (
    echo [WARN] light.exe failed ^(%ERRORLEVEL%^) -- MSI not built; the exe above is still fine.
    goto :after_msi
)
echo [INSTALLER OK]
echo   bin\amd64\WgSharp-Setup.msi  ^(version %VERSION%^)

rem --- Code signing: MSI (same optional certificate as the exe) -------
call :sign_file "bin\amd64\WgSharp-Setup.msi"

:after_msi

rem --- Optionally build the standalone/portable zip -------------------
rem  Packages the SAME WgSharp.exe (not a renamed copy -- mode is decided by
rem  run location, not filename) plus README and LICENSE into
rem  WgSharp-<version>-portable.zip. This is the "runs from its own folder"
rem  distribution; unzipped anywhere outside Program Files it runs portable.
rem  Override the prompt with PORTABLEZIP=1 (build) or PORTABLEZIP=0 (skip);
rem  interactive default (Enter) is Yes.
call :maybe_portable_zip

echo.
echo [BUILD OK]
echo   bin\amd64\WgSharp.exe
echo Remember: drop the matching amd64 wintun.dll next to the exe, or
echo let it auto-download on first launch.
echo The background service (Settings -^> "Start with Windows") is the
echo same exe, started by SCM instead of double-clicked -- no second file.
popd
exit /b 0

rem ============================================================
rem  :decide_wixpdb -- sets SPDB_FLAG to "-spdb" (suppress the .wixpdb) or
rem  empty (keep it). Honors the WIXPDB env var without prompting, else asks.
rem  Uses plain expansion only (no enabledelayedexpansion); safe because each
rem  line in a called subroutine is expanded as it runs.
rem ============================================================
:decide_wixpdb
set "SPDB_FLAG=-spdb"
if defined WIXPDB (
    if "%WIXPDB%"=="1" set "SPDB_FLAG="
    goto :eof
)
set "ANSWER="
set /p "ANSWER=Keep the .wixpdb installer symbol file? [y/N] "
if /i "%ANSWER%"=="y"   set "SPDB_FLAG="
if /i "%ANSWER%"=="yes" set "SPDB_FLAG="
goto :eof

rem ============================================================
rem  :maybe_portable_zip -- builds WgSharp-<version>-portable.zip in bin\amd64
rem  from the just-built exe plus README/LICENSE. Honors PORTABLEZIP (1=build,
rem  0=skip) without prompting, else asks (default Yes). Uses PowerShell's
rem  Compress-Archive (present on Windows 10+ / Server 2016+); if PowerShell
rem  isn't available it warns and skips rather than failing the build.
rem ============================================================
:maybe_portable_zip
if not exist "bin\amd64\WgSharp.exe" goto :eof

rem Decide whether to build, using FLAT statements only. A set /p read inside
rem a parenthesized ( ... ) block is substituted at parse time (before the
rem read happens) unless enabledelayedexpansion is on -- which this script
rem forbids -- so the prompt is done at the top level here, never in a block.
if not defined PORTABLEZIP goto :pz_ask
if "%PORTABLEZIP%"=="1" goto :pz_build
goto :eof
:pz_ask
set "ANSWER="
set /p "ANSWER=Build the standalone/portable zip? [Y/n] "
if /i "%ANSWER%"=="n"  goto :eof
if /i "%ANSWER%"=="no" goto :eof

:pz_build
rem Stage exactly what the portable distribution should contain, so the zip
rem has a clean flat layout (exe + docs) regardless of what else is in
rem bin\amd64 (the MSI, a .wixpdb, downloaded DLLs, etc.).
set "PZDIR=obj\portable"
if exist "%PZDIR%" rmdir /s /q "%PZDIR%"
mkdir "%PZDIR%" 2>nul
copy /y "bin\amd64\WgSharp.exe" "%PZDIR%\WgSharp.exe" >nul
if exist "README.md" copy /y "README.md" "%PZDIR%\README.md" >nul
if exist "LICENSE"   copy /y "LICENSE"   "%PZDIR%\LICENSE.txt" >nul

set "PZOUT=bin\amd64\WgSharp-%VERSION%-portable.zip"
if exist "%PZOUT%" del /q "%PZOUT%"

where powershell >nul 2>&1
if errorlevel 1 goto :pz_nops

rem Build the zip. Errors are shown (not hidden) so a real failure is
rem diagnosable instead of silently producing nothing.
echo Creating %PZOUT% ...
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%CD%\%PZDIR%\*' -DestinationPath '%CD%\%PZOUT%' -Force"
if errorlevel 1 goto :pz_fail
if not exist "%PZOUT%" goto :pz_fail
echo [PORTABLE ZIP OK]
echo   %PZOUT%
goto :eof

:pz_nops
echo [WARN] PowerShell not found -- skipping the portable zip. The staged
echo        contents are in %PZDIR% if you want to zip them manually.
goto :eof

:pz_fail
echo [WARN] Could not create the portable zip. Staged contents are in %PZDIR%.
echo        Try:  powershell -Command "Compress-Archive -Path '%PZDIR%\*' -DestinationPath '%PZOUT%' -Force"
goto :eof

rem ============================================================
rem  Optional Authenticode signing.
rem
rem  :sign_file <path>  -- signs the file with the certificate whose
rem  subject matches SIGN_CERT (default "SMCE"; override by setting the
rem  SIGN_CERT environment variable before running build.cmd).
rem
rem  Entirely optional and self-disabling:
rem    - If no matching certificate exists in the CurrentUser or
rem      LocalMachine "My" store, signing is skipped with a note and the
rem      build proceeds unsigned (so the script works unchanged on
rem      machines without the cert).
rem    - If the cert exists but signtool.exe can't be found, same skip.
rem    - If signtool itself fails (e.g. timestamp server unreachable),
rem      a warning is printed and the build continues -- an unsigned
rem      build beats no build, and the warning makes it non-silent.
rem
rem  signtool is located in this order:
rem    1. SIGNTOOL environment variable (full path to signtool.exe)
rem    2. newest "%ProgramFiles(x86)%\Windows Kits\10\bin\10.*\x64"
rem    3. anywhere on PATH
rem
rem  NOTE on being called this deep in the script: :sign_locate runs its
rem  detection ONCE (SIGN_CHECKED flag) and both call sites reuse the
rem  result. Everything uses plain (non-delayed) expansion -- `if defined`
rem  is evaluated dynamically by cmd, so the for-loops below work without
rem  enabledelayedexpansion, consistent with the header's warning about
rem  parens in paths.
rem ============================================================
:sign_file
if not defined SIGN_CHECKED call :sign_locate
if not "%SIGN_READY%"=="1" goto :eof
echo Signing %~1 ...
"%SIGNTOOL_EXE%" sign /n "%SIGN_CERT%" /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 "%~1"
if %ERRORLEVEL% neq 0 (
    echo [WARN] signtool returned %ERRORLEVEL% for "%~1" -- continuing with it UNSIGNED.
    echo        ^(Common cause: the timestamp server was unreachable. Re-run to retry.^)
) else (
    echo [SIGNED] %~1
)
goto :eof

:sign_locate
set "SIGN_CHECKED=1"
set "SIGN_READY="
if not defined SIGN_CERT set "SIGN_CERT=SMCE"

rem -- is the certificate installed? Detection must match signtool's /n
rem    semantics: /n matches a SUBSTRING of the certificate subject, so a
rem    cert named "SMCE Sp. z o.o." is signable with /n "SMCE". certutil's
rem    CertId lookup is (near-)exact on the CN and would false-negative on
rem    such certs, so we use PowerShell (already a build dependency for the
rem    date stamp) with the same substring rule, and require a private key
rem    since a public-only cert can't sign anything.
set "SIGN_CERT_FOUND="
powershell -NoProfile -NonInteractive -Command "exit [int](-not ((Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue) | Where-Object { $_.Subject -match [regex]::Escape($env:SIGN_CERT) -and $_.HasPrivateKey } | Measure-Object).Count)" >nul 2>&1 && set "SIGN_CERT_FOUND=1"
if not defined SIGN_CERT_FOUND (
    echo [SKIP] Code-signing certificate "%SIGN_CERT%" not found in the user or
    echo        machine certificate store -- outputs will not be signed.
    goto :eof
)

rem -- locate signtool.exe
set "SIGNTOOL_EXE="
if defined SIGNTOOL if exist "%SIGNTOOL%" set "SIGNTOOL_EXE=%SIGNTOOL%"
if not defined SIGNTOOL_EXE for /f "delims=" %%d in ('dir /b /ad /o-n "%ProgramFiles(x86)%\Windows Kits\10\bin\10.*" 2^>nul') do if not defined SIGNTOOL_EXE if exist "%ProgramFiles(x86)%\Windows Kits\10\bin\%%d\x64\signtool.exe" set "SIGNTOOL_EXE=%ProgramFiles(x86)%\Windows Kits\10\bin\%%d\x64\signtool.exe"
if not defined SIGNTOOL_EXE for /f "delims=" %%s in ('where signtool 2^>nul') do if not defined SIGNTOOL_EXE set "SIGNTOOL_EXE=%%s"
if not defined SIGNTOOL_EXE (
    echo [SKIP] Certificate "%SIGN_CERT%" is installed, but signtool.exe was not
    echo        found ^(is a Windows 10/11 SDK installed?^) -- outputs will not be signed.
    goto :eof
)

echo Code signing enabled: certificate "%SIGN_CERT%", tool: %SIGNTOOL_EXE%
set "SIGN_READY=1"
goto :eof
