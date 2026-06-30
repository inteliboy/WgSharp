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
rem   - VERSION       = 1.YY.MMDD     (3 fields) -- used for the MSI's
rem                     ProductVersion, since that field cannot hold a 4th
rem                     part at all.
rem   - VERSION_ASSEMBLY = 1.YY.MMDD.0 (4 fields) -- used for the exe's
rem                     AssemblyVersion/AssemblyFileVersion/
rem                     AssemblyInformationalVersion, which all support (and
rem                     in .NET's case, the first two REQUIRE) 4 parts. The
rem                     trailing ".0" is a fixed placeholder revision field
rem                     (there's never more than one build per day from this
rem                     script, so it's always 0) -- present purely so the
rem                     format is uniform and explicit, not because it ever
rem                     varies.
rem Either way, the same YY/MM/DD digits are what's actually meaningful, and
rem VERSION is exactly the leading 3 fields of VERSION_ASSEMBLY -- the
rem closest thing to "one standard" achievable given the MSI constraint.
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
set VERSION=1.%YY%.%MM%%DD%
set VERSION_ASSEMBLY=%VERSION%.0
echo Version stamp: %VERSION_ASSEMBLY% ^(exe^) / %VERSION% ^(MSI^)

> "src\core\AssemblyInfo.generated.cs" (
    echo // Auto-generated by build.cmd on every build -- do not edit by hand.
    echo // Stamps the exe with today's date as its version: 1.YY.MMDD.0.
    echo using System.Reflection;
    echo [assembly: AssemblyVersion^("%VERSION_ASSEMBLY%"^)]
    echo [assembly: AssemblyFileVersion^("%VERSION_ASSEMBLY%"^)]
    echo [assembly: AssemblyInformationalVersion^("%VERSION_ASSEMBLY%"^)]
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
    /recurse:src\core\*.cs /recurse:src\crypto\*.cs /recurse:src\proto\*.cs /recurse:src\net\*.cs /recurse:src\tun\*.cs /recurse:src\ui\*.cs ^
    "src\svc\WgSharpService.cs" ^
    "src\Program.cs"
if %ERRORLEVEL% neq 0 (
    echo [BUILD FAILED] amd64 build returned %ERRORLEVEL%.
    popd
    exit /b %ERRORLEVEL%
)
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

"%WIXBIN%\light.exe" -nologo ^
    -ext WixUIExtension -ext WixUtilExtension ^
    -sice:ICE61 ^
    -out "bin\amd64\WgSharp-Setup.msi" ^
    "obj\Product.wixobj"
if %ERRORLEVEL% neq 0 (
    echo [WARN] light.exe failed ^(%ERRORLEVEL%^) -- MSI not built; the exe above is still fine.
    goto :after_msi
)
echo [INSTALLER OK]
echo   bin\amd64\WgSharp-Setup.msi  ^(version %VERSION%^)
:after_msi

echo.
echo [BUILD OK]
echo   bin\amd64\WgSharp.exe
echo Remember: drop the matching amd64 wintun.dll next to the exe, or
echo let it auto-download on first launch.
echo The background service (Settings -^> "Start with Windows") is the
echo same exe, started by SCM instead of double-clicked -- no second file.
popd
exit /b 0
