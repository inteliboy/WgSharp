# Closes a running WgSharp GUI and stops the WgSharpSvc background service
# so Setup can safely overwrite WgSharp.exe. Used by installer\WgSharp.nsi
# both before install (fresh install or update) and before uninstall -
# the exact same two mechanisms the old MSI installer's
# <ServiceControl Stop="both" Wait="yes"> and <util:CloseApplication
# EndSessionMessage="yes" TerminateProcess="1"> gave (see the removed
# installer\Product.wxs for that history).
param(
    [int]$TimeoutSeconds = 15,
    [string]$InstallDir = ''
)

$ErrorActionPreference = 'SilentlyContinue'

# --- Stop the background service first ---------------------------------
# Same service ServiceInstaller.cs registers/controls at runtime (name
# WgSharpSvc, LocalSystem, "<exe> --service"). Stopping it here releases
# its lock on WgSharp.exe and tears its tunnel down cleanly before Setup
# replaces the file.
$svc = Get-Service -Name 'WgSharpSvc' -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') {
    try { Stop-Service -Name 'WgSharpSvc' -Force -ErrorAction SilentlyContinue } catch { }
    try { $svc.WaitForStatus('Stopped', (New-TimeSpan -Seconds 30)) } catch { }
}

# --- Close the interactive GUI, if running ------------------------------
# WgSharpSvc runs the SAME WgSharp.exe (as SYSTEM, in Session 0), so a bare
# "Get-Process -Name WgSharp" would also match it; SessionId -ne 0 isolates
# the actual interactive/tray instance(s) from the service process.
$guiProcs = Get-Process -Name 'WgSharp' -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -ne 0 }
if ($guiProcs) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class WgSharpCloser
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    private const uint GW_OWNER = 4;
    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint ENDSESSION_CLOSEAPP = 0x00000001;

    // WgSharp's own MainForm.OnFormClosing only treats CloseReason.UserClosing
    // (an ordinary WM_CLOSE) as "minimize to tray, don't exit" - a bare
    // taskkill or Process.CloseMainWindow() sends exactly that message and
    // gets swallowed. WM_QUERYENDSESSION + WM_ENDSESSION is what .NET
    // WinForms maps to CloseReason.WindowsShutDown instead, which the app
    // already treats as "really exit" and runs its synchronous tunnel
    // teardown for - the same mechanism the old MSI installer's
    // util:CloseApplication EndSessionMessage="yes" relied on.
    public static int CloseTopLevelWindows(int pid)
    {
        List<IntPtr> targets = new List<IntPtr>();
        EnumWindows((hWnd, lParam) =>
        {
            uint windowPid;
            GetWindowThreadProcessId(hWnd, out windowPid);
            if (windowPid == (uint)pid && GetWindow(hWnd, GW_OWNER) == IntPtr.Zero)
                targets.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        foreach (IntPtr hWnd in targets)
        {
            IntPtr result;
            SendMessageTimeout(hWnd, WM_QUERYENDSESSION, IntPtr.Zero, (IntPtr)ENDSESSION_CLOSEAPP,
                SMTO_ABORTIFHUNG, 5000, out result);
            SendMessageTimeout(hWnd, WM_ENDSESSION, (IntPtr)1, (IntPtr)ENDSESSION_CLOSEAPP,
                SMTO_ABORTIFHUNG, 5000, out result);
        }
        return targets.Count;
    }
}
'@ -ErrorAction SilentlyContinue

    # If the helper failed to compile, skip the polite close and fall through
    # to the forced termination below instead of aborting the whole script.
    foreach ($p in $guiProcs) {
        try { [WgSharpCloser]::CloseTopLevelWindows($p.Id) | Out-Null } catch { }
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $stillRunning = Get-Process -Name 'WgSharp' -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -ne 0 }
        if (-not $stillRunning) { break }
        Start-Sleep -Milliseconds 300
    }

    # Guaranteed fallback, matching the old MSI's TerminateProcess="1"/
    # Timeout="15": force-kill any interactive survivor so Setup never fails
    # to replace a locked WgSharp.exe. SessionId filter again, so a
    # legitimately still-running SYSTEM service process (e.g. because
    # Stop-Service above failed) is never collaterally killed here.
    Get-Process -Name 'WgSharp' -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -ne 0 } |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

# --- Final guarantee: nothing holds WgSharp.exe, and it is writable -----
# A service that reports "Stopped" can still have its process alive for a
# moment (or Stop-Service can time out while the tunnel tears down), and a
# GUI can survive the steps above. Setup used to start copying right away
# and failed with "Error opening file for writing". Now: kill whatever
# WgSharp process still runs from the install directory (any session), then
# wait until the exe can actually be opened for writing before returning.
function Get-InstallProcs {
    Get-Process -Name 'WgSharp' -ErrorAction SilentlyContinue | Where-Object {
        $path = $null
        try { $path = $_.MainModule.FileName } catch { }
        # Unreadable path: assume it is ours (safer for the install to proceed).
        (-not $path) -or (-not $InstallDir) -or $path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase)
    }
}

function Test-ExeWritable {
    if (-not $InstallDir) { return $true }
    $exe = Join-Path $InstallDir 'WgSharp.exe'
    if (-not (Test-Path -LiteralPath $exe)) { return $true }
    try {
        $fs = [System.IO.File]::Open($exe, 'Open', 'ReadWrite', 'None')
        $fs.Close()
        return $true
    } catch { return $false }
}

$deadline = (Get-Date).AddSeconds(30)
while ($true) {
    $left = @(Get-InstallProcs)
    if ($left.Count -gt 0) {
        $left | Stop-Process -Force -ErrorAction SilentlyContinue
        # Last resort for a stubborn service process.
        & sc.exe stop WgSharpSvc 2>&1 | Out-Null
    }
    if ($left.Count -eq 0 -and (Test-ExeWritable)) { break }
    if ((Get-Date) -gt $deadline) { break }
    Start-Sleep -Milliseconds 400
}

exit 0
