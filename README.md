<div align="center">

# WgSharp

**A from-scratch WireGuard® client for Windows — pure C#, zero dependencies, no MSBuild.**

[![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D6?logo=windows&logoColor=white)](#run)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4?logo=dotnet&logoColor=white)](#build)
[![Language](https://img.shields.io/badge/language-C%23%205-178600)](#architecture)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Build](https://img.shields.io/badge/build-csc.exe%20only-yellow)](#build)

Hand-written crypto. A hand-written Noise IKpsk2 handshake. A hand-written
transport data path. A WinForms GUI. All compiled with `csc.exe` alone —
**no MSBuild, no `.csproj`, no NuGet, no internet access required to build.**

</div>

> [!NOTE]
> **Status:** functional and actively developed. The managed data path (with
> Windows CNG-accelerated ChaCha20-Poly1305 where available), the WireGuardNT
> kernel backend, the WFP kill-switch, and multi-peer routing are all validated
> on real hardware. See [Current scope and limitations](#current-scope-and-limitations).

---

## Table of contents

- [Features](#features)
- [Screenshots](#screenshots)
- [Build](#build)
- [Run](#run)
- [Sample configuration](#sample-configuration)
- [Two tunnel backends](#two-tunnel-backends)
- [AmneziaWG (censorship-resistance obfuscation)](#amneziawg-censorship-resistance-obfuscation)
- [Kill-switch](#kill-switch)
- [Background service: reconnect before login](#background-service-reconnect-before-login)
- [Settings](#settings)
- [Tunnel list: import, drag-and-drop, double-click to connect](#tunnel-list-import-drag-and-drop-double-click-to-connect)
- [Scanning a QR code](#scanning-a-qr-code)
- [Portable mode and passwords](#portable-mode-and-passwords)
- [Architecture](#architecture)
- [Cryptography: what's verified](#cryptography-whats-verified)
- [Current scope and limitations](#current-scope-and-limitations)
- [Disclaimer](#disclaimer)
- [License](#license)
- [Acknowledgements](#acknowledgements)
- [Support my work](#support-my-work)

---

## Features

| | |
|---|---|
| 🔀 **Two interchangeable backends** | A fully from-scratch managed implementation, and an optional kernel-mode backend driven by the official WireGuardNT driver for higher throughput. |
| ⚡ **Hardware-accelerated crypto** | The managed backend automatically uses Windows CNG (`BCryptEncrypt` / `BCryptDecrypt`) for ChaCha20-Poly1305 on Windows 10 1709+, giving 3-6× higher throughput than the pure managed path. Falls back transparently to the hand-written implementation on older Windows. |
| 🛡️ **Leak-free kill-switch** | Built on the Windows Filtering Platform (WFP) with weighted permit/block filters — not a netsh firewall-rule approximation. |
| 🔑 **Multi-peer & cryptokey routing** | mac2/cookie replies, endpoint re-resolution, persistent keepalive, full handshake/transport framing. |
| 🌐 **Split-tunnel DNS auto-fix** | If the configured DNS server isn't covered by AllowedIPs, WgSharp automatically routes it through the tunnel so DNS works without requiring manual config changes. |
| 🧯 **Self-authorizing firewall rules** | The one-time setup pre-registers the exe with Windows Firewall, so the interactive "allow this app" prompt never interrupts you. |
| 🚀 **Background service** | Reconnects your last tunnel *before login*, the same model the official client uses — a real `LocalSystem` service with the GUI as a thin client over a named pipe. |
| 🙅 **No UAC prompt on launch** | The installed GUI runs unelevated (`asInvoker`). The MSI registers and starts the background service at install time, so an MSI install never prompts on launch at all; a non-MSI installed copy sets the service up on first Activate via a single UAC prompt. Portable mode instead asks you to run it as administrator (see [Portable mode](#portable-mode-and-passwords)). |
| 🖥️ **GUI autostart** | Optionally launches the tray app at login too, independent of the boot-time service. |
| 📦 **Drag-and-drop import** | `.conf` / `.zip` / `.wgsp` straight onto the tunnel list. |
| 🔒 **Portable mode** | Password-encrypted configs that travel with the app folder, with per-tunnel passwords cached for the session. |
| 📊 **Live stats** | Upload/download charts, session duration, total transferred, and tunnel latency. |
| ✍️ **Config editor** | Syntax highlighting, a live-derived public key, and QR export. |
| 📷 **Scan from QR code** *(experimental)* | Add a tunnel by pointing the webcam at a QR code (or scanning a saved image) — a from-scratch QR decoder, no external library. Enabled via **Settings → Show experimental features**. |
| 🔔 **Update notifications** | Quietly checks GitHub at startup for a newer release and shows a clickable tray notification if one exists — informational only, never auto-downloads. Opt out via **Settings → Check for updates**. |
| 🧰 **System tray** | Live status tooltip, quick-connect menu, closing the window minimizes instead of exiting. |
| ℹ️ **About dialog** | Version, GitHub link, and Buy Me a Coffee support link — accessible from the window's system menu. |

## Screenshots

<p align="center">
  <img width="762" height="502" alt="WgSharp screenshot 1" src="https://github.com/user-attachments/assets/2f565607-d407-4db4-85b8-dde3063e747e" />
  <img width="762" height="502" alt="WgSharp screenshot 2" src="https://github.com/user-attachments/assets/eba9b9f0-846f-4602-9bde-cb352b4d7082" />
</p>
<p align="center">
  <img width="762" height="502" alt="WgSharp screenshot 3" src="https://github.com/user-attachments/assets/e1ad4fb5-cdc5-49ee-94f6-69d9848053f6" />
  <img width="762" height="502" alt="WgSharp screenshot 4" src="https://github.com/user-attachments/assets/6b834b21-5dc9-4893-96c1-461905e9eded" />
</p>

## Build

```bat
build.cmd
```

This locates `csc.exe` under `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\`
and produces a single build — `bin\amd64\WgSharp.exe` — stamped with a
`1.YY.MMDD.0` version (see [Versioning](#versioning) below). WgSharp targets
x64 only (see [Run](#run)). The same exe doubles as the optional background
service (see [Background service](#background-service-reconnect-before-login))
— no second file. The script uses relative paths and lets `csc` expand each
`src\<folder>\*.cs` wildcard itself, so it's immune to spaces or parentheses
in the install path.

### Versioning

Every build is stamped with today's date, generated fresh by `build.cmd`
(via `src\core\AssemblyInfo.generated.cs`, regenerated and overwritten on
every run, not checked into source control). No separate version number to
remember to bump: build it today, it's today's date; build it again next
month, it's that date.

There isn't one identical version string used absolutely everywhere, because
Windows Installer's `ProductVersion` field is a hard wall: it's strictly
3-part (`major.minor.build`) and WiX rejects a 4th field outright, while
.NET's `AssemblyVersion`/`AssemblyFileVersion` are 4-part. So `build.cmd`
computes ONE date encoding and uses it in both of the only two forms Windows
actually allows:

- **`1.YY.MMDD.0`** (4 fields) for the exe's `AssemblyVersion`,
  `AssemblyFileVersion`, and `AssemblyInformationalVersion` — e.g.
  `1.26.0629.0` for 2026-06-29. The trailing `.0` is a fixed placeholder
  revision field (there's only ever one build per day from this script).
- **`1.YY.MMDD`** (3 fields — exactly the first 3 fields of the line above)
  for the MSI's `ProductVersion`.

Neither can hold a 4-digit year (both pack versions into fixed-width numeric
fields), so the year is shortened to its last two digits (`YY`, good until
2255), and month+day are zero-padded into a single unambiguous 4-digit
`MMDD` number — zero-padding here isn't cosmetic, it's required for
correctness: without it, month 1/day 23 and month 12/day 3 would both
produce the same digits ("123").

### MSI installer (optional)

If [WiX Toolset v3.14](https://wixtoolset.org/) is installed, `build.cmd` also
produces `bin\amd64\WgSharp-Setup.msi` right after the exe — installs to a
**fixed** location, `Program Files\WgSharp` (there's no "choose a folder"
dialog; see why below), adds a Start Menu shortcut, and registers normally in
Add/Remove Programs. The source is `installer\Product.wxs`, built directly
with `candle.exe`/`light.exe` (no Visual Studio WiX project, same
no-MSBuild philosophy as the rest of the build).

To get it: download and run
[**wix314.exe**](https://github.com/wixtoolset/wix3/releases/download/wix3141rtm/wix314.exe)
from the
[wixtoolset/wix3 releases page](https://github.com/wixtoolset/wix3/releases)
(the v3.14.1 RTM build — that's a "v3", not the newer, MSBuild-based WiX v4/v5,
which works differently and isn't what `build.cmd` looks for). It's a normal
installer; nothing else to configure afterward — `build.cmd` finds it
automatically via the `%WIX%` environment variable that installer sets.

This step is **best-effort**: if WiX isn't found (checked via that `%WIX%`
environment variable, then the usual Program Files path), `build.cmd` skips
it with a clear `[SKIP]` message and the exe build above is unaffected either
way — the MSI is a convenience, not a requirement.

**Optional code signing.** After building the exe (and, if present, the MSI),
`build.cmd` Authenticode-signs both if a code-signing certificate is installed
— matched by subject substring (default `SMCE`; override with the `SIGN_CERT`
environment variable) the same way `signtool /n` matches. It signs the exe
*before* the MSI is built so the installer packs the signed binary. This is
entirely self-disabling: no matching certificate, or no `signtool.exe` on the
machine, and signing is skipped with a note while the build proceeds unsigned;
a signing failure (e.g. an unreachable timestamp server) warns but never fails
the build. `signtool.exe` is located via a `SIGNTOOL` environment variable, the
newest Windows 10/11 SDK, or `PATH`, in that order.

**Two interactive prompts** near the end (both skippable via an environment
variable, so unattended/CI builds never block):

- *Keep the `.wixpdb`?* — `light.exe` writes a `WgSharp-Setup.wixpdb` symbol
  file next to the MSI. It's only useful for authoring binary MSP patches
  between MSI versions (which this project doesn't do — it ships full upgrade
  MSIs), so the default is **No** (suppressed with `-spdb`). Set `WIXPDB=1` to
  keep it or `WIXPDB=0` to suppress it without being asked.
- *Build the portable zip?* — packages the **same** `WgSharp.exe` (the mode is
  decided by run location, not filename, so there's no separate "portable exe")
  together with `README.md` and `LICENSE` into
  `bin\amd64\WgSharp-<version>-portable.zip`. Default **Yes**. Set
  `PORTABLEZIP=1`/`0` to force it on/off. Unzipped anywhere outside
  `Program Files`, that copy runs in portable mode.

**Installing or upgrading automatically closes a running WgSharp** — both the
GUI and the background service, which are the same exe — before touching
files:

- The **background service** (`WgSharpSvc`) is stopped via a `<ServiceControl
  Stop="both" Wait="yes">` element — a core Windows Installer feature where
  the engine itself calls the Service Control Manager and synchronously waits
  for the service to actually exit before files are written. Its own shutdown
  path stops any active tunnel cleanly (kill-switch rules and routes removed)
  as part of that.
- The **GUI/tray process** is closed via `util:CloseApplication`, with
  `EndSessionMessage="yes"` rather than a plain `CloseMessage` alone: a bare
  `WM_CLOSE` arriving from another process is indistinguishable, at the
  WinForms level, from the user clicking the title-bar X, so it would get
  swallowed by the intentional "X minimizes to tray" behavior. `WM_QUERYENDSESSION`
  (what `EndSessionMessage` sends) maps to a different, unambiguous close
  reason that `MainForm` already treats as a real exit. As a hard guarantee on
  top of that, `TerminateProcess="1"` force-closes the process if it's still
  running after a few seconds — so the file is unlocked no matter what.

Note that `util:CloseApplication` has no UI of its own — these mechanisms work
silently in the background, without showing a prompt. Windows Installer's
*own* Restart Manager, however, does its file-lock detection early and, left
enabled, could pop the native **"Setup was unable to close all running
processes"** dialog before our own (more capable) shutdown even ran — racing
the service's several-second tunnel-teardown and losing. `Product.wxs`
therefore sets `MSIRESTARTMANAGERCONTROL=Disable`, handing shutdown entirely
to the two explicit mechanisms above: `ServiceControl` (`Wait="yes"`, so the
engine waits for the service teardown) and `util:CloseApplication`
(`Timeout="15"`, then a `TerminateProcess` fallback). The service's own
`OnStop` also calls `RequestAdditionalTime` and bounds its teardown on a
worker thread, so a slow adapter/route cleanup is never mistaken for a hung
service and can't stall the installer.

Together these guarantee `WgSharp.exe` is genuinely unlocked by the time
Setup writes the new one, with nothing depending on whether — or how — the
user interacts with any prompt.

It uses `1.YY.MMDD` — the leading 3 fields of the exe's own `1.YY.MMDD.0`
(see [Versioning](#versioning) above) — both stamped from one computation in
`build.cmd`, so there's only ever one date encoding to think about, even
though Windows Installer's stricter 3-field format keeps the two strings
from being byte-for-byte identical.

> [!NOTE]
> Since the version only has day granularity, rebuilding and reinstalling
> more than once on the same day produces a new MSI with a different
> ProductCode but an *identical* ProductVersion to the one already
> installed. Windows Installer doesn't treat that as an upgrade by default —
> it treats same-version + different-ProductCode as two unrelated products,
> which silently leaves the old `WgSharp.exe` untouched no matter how
> cleanly the running processes were closed first. `Product.wxs` sets
> `AllowSameVersionUpgrades="yes"` specifically to fix this (with WiX's
> ICE61 validation suppressed in `build.cmd`, since it's correctly flagging
> exactly the thing being asked for on purpose).

**Installing via the MSI changes WgSharp's defaults**, on the principle that
a proper install implies "set this up the way I'd actually want it running" —
see `src\core\InstallLocation.cs`, which detects whether the running exe lives
at that exact fixed path:

- The **background service** is registered *and started by the MSI itself*
  at install time (it's already elevated), so the unelevated GUI never has to
  prompt just to set it up — an MSI install shows no UAC prompt on launch at
  all. **Start GUI at login** is also enabled as the intended starting point.
  You can turn either back off afterward.
- **Portable mode is unavailable**, persistently, not just at first run — its
  checkbox in Settings is disabled outright. Portable mode is for the
  standalone/zip distribution that travels with its own folder; it doesn't
  fit a real Program Files install. Use the plain exe from the zip if you
  need portable mode.

None of this applies to a copy run from anywhere else (the zip distribution,
a USB stick, a dev build) — those keep today's defaults (everything off,
portable mode available) exactly as before. The installer registers and
starts the background service (via WiX `ServiceInstall` / `ServiceControl`),
but does **not** touch the Windows Firewall or enable login autostart at the
MSI level — firewall pre-authorization is the service's / app's own job, and
autostart is set by the app on first run.

**Uninstalling removes everything**, including the two files the MSI never
technically installed in the first place: `wintun.dll` and `wireguard.dll`
are downloaded by WgSharp itself at runtime (see
[Run](#run) below) straight into its own folder, so a plain uninstall
wouldn't know to remove them or be able to delete the now-non-empty install
folder. `Product.wxs` explicitly cleans both up on uninstall for exactly
that reason, alongside the background service registration and the
install directory itself.

## Run

**The installed build runs without a UAC prompt.** Its manifest requests
`asInvoker`, so launching `WgSharp.exe` shows no elevation dialog. All the
privileged work (creating the adapter, routes, DNS, firewall) happens in the
`WgSharpSvc` background service, which runs as `LocalSystem`. The first time
you need it, WgSharp performs a **one-time** administrator setup: a single UAC
prompt that registers and starts that service and pre-authorizes the firewall.
(With the MSI installer this already happened at install time, so there's no
prompt at all — see [MSI installer](#msi-installer-optional).) Every launch
after that is prompt-free, and controlling tunnels needs no
elevation — the service authorizes control commands by checking that your
Windows account is a member of the Administrators group, including via the UAC
*linked token*, so an admin's unelevated GUI is accepted without a prompt while
a standard user is refused.

**Portable / non-installed use needs administrator rights.** A portable copy
installs no service by design, so it must run elevated to create the adapter
itself. If you launch a portable copy unelevated, WgSharp offers to relaunch
itself as administrator (**Yes** closes and reopens it with a UAC prompt).
Choosing **No** simply closes WgSharp — portable mode can't create the adapter
without elevation, so rather than leave a window that can't do anything, it
exits and you can start it yourself with **right-click → Run as administrator**.
Once elevated, a portable copy runs the tunnel in-process, touching nothing on
the machine.
Portable mode and the background service are mutually exclusive: portable never
uses (or installs) the service, and an installed copy in `Program Files` can't
switch to portable mode.

On first startup, the background service downloads the native drivers it needs
in the background (the installed build does this inside the service as
`LocalSystem`; an elevated portable copy does it itself):

- `wintun.dll` — always, verified against a pinned SHA-256.
- `wireguard.dll` (WireGuardNT) — best-effort, for the optional kernel backend,
  verified by Authenticode signature and publisher (see [License](#license)).

You can also drop either DLL next to the executable yourself to skip the
download. Routine download/verification log lines only show up in the Log
tab when **Settings → Debug log** is on — by default you just see whether the
tunnel came up, not the driver-fetch plumbing behind it.

WgSharp ships an **amd64 (x64) build only**, and runs on x64 Windows only.
Wintun and WireGuardNT both install a kernel-mode driver, and Windows' CPU
emulation only covers user-mode code — so an x64 build running under emulation
(e.g. on ARM64) will never reach a working driver. WgSharp checks the machine's
real underlying architecture at startup via `IsWow64Process2` and refuses to run
on anything but native x64 rather than fail confusingly inside adapter
creation.

**Auto-reconnect after upgrade or restart:** if you were connected to a tunnel
when WgSharp last exited (or when an MSI upgrade restarted it), WgSharp
reconnects to that tunnel automatically on next launch. The last-tunnel record
is stored in `HKEY_LOCAL_MACHINE\Software\WgSharp` (registry), which the MSI
installer never touches. On first launch after an upgrade, WgSharp sees the
record, finds the tunnel still in the config store, and activates it — the
same experience as if you'd clicked Connect yourself. Only applies to
non-portable tunnels (the service can't decrypt portable configs unattended).

## Sample configuration

WgSharp reads standard WireGuard `.conf` files — the same format as the
official client. Use **Import** (or drag-and-drop) to load one, or **Add
tunnel** to paste/edit one directly in the built-in syntax-highlighting editor.

```ini
# Sample WireGuard configuration for WgSharp.
# Replace the placeholder keys/endpoint with your own. Keys are base64
# (44 characters ending in '='), exactly as produced by `wg genkey` / `wg pubkey`.

[Interface]
# Your client's private key.
PrivateKey = AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=
# The address (and prefix) assigned to this client inside the tunnel.
Address = 10.0.0.2/32
# Optional: pin a local UDP source port (0 / omitted = ephemeral).
# ListenPort = 51820
# Optional: DNS server(s) to use while the tunnel is active (comma-separated;
# entries that aren't IP addresses are treated as search domains).
# DNS = 10.0.0.1
# Optional: override the tunnel interface's MTU (0 / omitted = auto-derived
# from the default-route interface at connect time).
# MTU = 1420

[Peer]
# The server's public key.
PublicKey = BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=
# Optional pre-shared key for an extra symmetric layer.
# PresharedKey = CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC=
# The server's public endpoint (host:port). Hostnames are resolved to IPv4.
Endpoint = vpn.example.com:51820
# Which destinations route through the tunnel. 0.0.0.0/0 = full tunnel.
AllowedIPs = 0.0.0.0/0
# Send a keepalive every N seconds (useful behind NAT).
PersistentKeepalive = 25
```

> [!TIP]
> Multiple `[Peer]` sections in one file are fully supported — WgSharp routes
> by AllowedIPs (longest-prefix-match cryptokey routing), the same as the
> official client.

## Two tunnel backends

| | Managed (default) | WireGuardNT |
|---|---|---|
| Data path | Hand-written C# crypto/transport | Official kernel-mode driver (`wireguard.dll`) |
| Crypto | CNG-accelerated (Win 10 1709+), managed fallback | Kernel SIMD (driver handles it) |
| Throughput | Good — CNG; higher than pure managed | Higher — kernel data path |
| Adapter | Wintun | WireGuardNT's own miniport |
| AWG support | ✅ (always managed for AWG configs) | ❌ (driver can't speak AWG framing) |

Switch between them in **Settings → Use WireGuardNT (kernel) backend**. Each
backend uses its own deterministic adapter identity, so switching back and
forth doesn't confuse Windows' network-profile matching. If a previous run of
the WireGuardNT backend didn't shut down cleanly, it automatically recovers by
opening the existing adapter instead of requiring a reboot.

## AmneziaWG (censorship-resistance obfuscation)

WgSharp can speak [AmneziaWG](https://docs.amnezia.org/documentation/amnezia-wg/)
("AWG"), a wire-format extension that disguises a WireGuard connection from
DPI/firewall signature matching — without changing the underlying handshake
or transport cryptography at all. The actual Noise_IKpsk2 handshake and
ChaCha20-Poly1305 transport are byte-for-byte standard WireGuard the entire
time; AWG only adds, at the network I/O boundary:

- **Junk packets** (`Jc`/`Jmin`/`Jmax`) — random noise sent before every
  handshake attempt, perturbing the connection's size/timing signature.
- **Header obfuscation** (`H1`–`H4`) — replaces the fixed 4-byte message-type
  header WireGuard normally uses (the exact thing most DPI signature matching
  keys on) with four custom values you choose.
- **Padding** (`S1`/`S2`) — extra random bytes prepended to the handshake
  messages, so they're no longer the WireGuard-standard 148/92 byte sizes.

**It's recognized automatically from the config file, not a setting you flip.**
Add any of `Jc`, `Jmin`, `Jmax`, `S1`, `S2`, `H1`, `H2`, `H3`, `H4` to a
tunnel's `[Interface]` section (the same keys Amnezia's own apps and AWG
server setup scripts already generate) and WgSharp treats that tunnel as AWG
automatically — no manual toggle, so an AWG config someone hands you just
works without them needing to tell you to enable anything:

```ini
[Interface]
PrivateKey = ...
Address = 10.8.1.2/32
Jc = 4
Jmin = 40
Jmax = 70
S1 = 0
S2 = 0
H1 = 1234567891
H2 = 1234567892
H3 = 1234567893
H4 = 1234567894

[Peer]
PublicKey = ...
Endpoint = vpn.example.com:51820
AllowedIPs = 0.0.0.0/0
```

`Jc`/`Jmin`/`Jmax` must all be present together (or all omitted), same for
`H1`–`H4`; WgSharp rejects a config that only sets some of a group, since a
partial set is almost always a typo, and the most common failure mode for a
mismatched value isn't an error — it's a handshake that silently never
completes. **Both ends of the tunnel must use identical values** for all of
these, the same way the static keys themselves must match; there's no
negotiation.

> [!IMPORTANT]
> **AWG tunnels always run on the managed backend, never WireGuardNT** —
> regardless of the Settings → Use WireGuardNT toggle. WireGuardNT is
> WireGuard LLC's own closed-source, digitally signed kernel driver; WgSharp
> doesn't own that code and can't change its wire format, so it will never be
> able to speak AWG's disguised framing. The managed backend is the
> implementation WgSharp wrote from scratch, where the wire format is
> genuinely ours to extend. This is enforced automatically
> (`TunnelBackendFactory`) and surfaced in both the Log tab and the tunnel's
> own detail panel ("Obfuscation: AmneziaWG (managed backend only)").

A small but deliberate design choice: an AmneziaWG config is only useful if
*you also control (or can influence) the server side*, since a stock
WireGuard server has no idea what to do with junk packets, custom headers, or
padded handshakes — it would just drop them as malformed. If you don't run
the server, ask whoever does whether they offer AWG-compatible endpoint
details before relying on this.

## Kill-switch

The kill-switch blocks all traffic that isn't going through the tunnel,
engaged automatically once `AllowedIPs` includes a full-tunnel default route.

It's implemented with the **Windows Filtering Platform** directly (P/Invoke
against `fwpuclnt.dll`), not netsh firewall rules. This matters: Windows
Defender Firewall always lets a block rule win over a competing allow rule,
regardless of how specific the allow rule's scope is, so a netsh-based
block-all-plus-scoped-allow kill-switch can never reliably pass real tunnel
traffic. WFP has no such categorical rule — filters in WgSharp's own sublayer
are arbitrated purely by weight (permit outranks block), so the permit
deterministically wins. The WFP session is dynamic, so every filter is
automatically removed if the engine handle closes or the process dies — no
stuck rules, no requiring a reboot to recover network access.

## Background service: reconnect before login

WgSharp runs the actual tunnel inside a Windows Service (`WgSharpSvc`,
`LocalSystem`) that starts at boot — before anyone logs in — following the
same model the official WireGuard for Windows client uses. This is also what
lets the GUI run unelevated: the service owns everything that needs
administrator rights, and the GUI is a thin client driving it over a named
pipe.

The service is an **always-running manager** (registered `start= auto`), idle
when no tunnel is active. Activation and deactivation are pipe commands
(`ACTIVATE` / `DEACTIVATE`), not Service Control Manager start/stop — that's
the change that removes the per-launch UAC prompt, since driving SCM would
require an elevated GUI. Two design points, both hard-won:

- **Control commands are admin-gated.** `ACTIVATE`, `DEACTIVATE`, and the
  config-write commands are only honored if the pipe client's user is in the
  Administrators group, checked against the client's UAC *linked* token so an
  admin's unelevated GUI passes with no prompt and a standard user is refused.
  Read-only queries (status, transfer counters, handshake time, log) stay open
  to any authenticated user.
- **A control command never blocks on the bring-up.** The service validates
  the request and replies instantly; the multi-second adapter/route work runs
  on its own worker thread, and the GUI follows progress by polling status
  (exactly as it always did). An earlier design held the pipe open for the
  whole bring-up and timed out; a later one made activation an SCM *start* and
  forced the GUI to be elevated. This model avoids both.

It's the same `WgSharp.exe`, not a second file: `Main()` checks for a
`--service` argument (which the service registration includes, so SCM always
launches it that way) and runs as the service instead of the GUI in that case.
A separate `--elevated-setup` argument is what the one-time UAC prompt runs to
register + start the service and pre-authorize the firewall.

Turn it on (or off) via **Settings → Start with Windows (background service)**.
When a tunnel is connected through the service, the service records it as the
"last tunnel" (in `HKEY_LOCAL_MACHINE\Software\WgSharp`), and re-activates it
at the next boot — reconnecting before login; an explicit Deactivate clears
that record, so a disconnected tunnel stays disconnected across reboots. On
launch the GUI also checks whether the service is already running a tunnel
(most notably one auto-reconnected at boot, before you logged in) and reflects
that immediately.

**This only works for non-portable tunnels.** Non-portable configs are already
encrypted with machine-scoped DPAPI (decryptable by any admin/SYSTEM process on
the box, which is exactly what a pre-login service is), so the service can read
them with no one present. Portable mode is password-protected by design —
there's no human at a boot-time service to type that password — so portable
tunnels always run in the (elevated) GUI itself, never through the service.

## Settings

- **Portable mode** — store tunnel configs in a `conf` folder next to the exe,
  password-encrypted, instead of the DPAPI-protected `C:\ProgramData` store.
  Unavailable when running from the MSI's fixed install location — see
  [MSI installer](#msi-installer-optional).
- **Use WireGuardNT (kernel) backend** — see [above](#two-tunnel-backends).
- **Start with Windows (background service)** — see
  [above](#background-service-reconnect-before-login). Disabled when
  Portable mode is on, since the service can't use portable tunnels.
- **Start GUI at login (in the tray)** — launches the WgSharp window
  minimized to the notification area when you log in, like the official
  client. A per-user `HKCU\...\Run` entry (with a `--tray` argument); no
  elevation needed. Independent of the background service: that reconnects
  your tunnel *before* login, this just puts the tray icon there for you
  *after* you log in.
- **Debug log** — when on, the Log tab shows full diagnostic detail (including
  routine driver-download/verification lines for Wintun and WireGuardNT) and
  the background service writes a `service.log` file. When off (the default)
  only the most meaningful messages are shown, and **no `service.log` is
  written** at all — the GUI instead pulls the service's recent activity from
  a small in-memory buffer over the control pipe, so there's no constant disk
  I/O or ever-growing file.
- **Show experimental features** — unhides features that work but aren't
  reliable on all hardware. Currently this only affects **Scan from QR code**
  in the Add Tunnel menu, which depends on the legacy VFW webcam API and may
  not function with every camera driver. **Show QR** on a selected tunnel
  (the QR icon in the toolbar) is always available regardless of this setting
  — it's just rendering an image from text, no hardware involved.
- **Check for updates** — when on (the default), WgSharp quietly asks GitHub at
  startup whether a newer release exists and, if so, shows a tray notification;
  clicking it opens the [releases page](https://github.com/inteliboy/WgSharp/releases)
  in your browser. It's entirely informational — WgSharp never downloads or
  installs an update itself. The check runs on a background thread, forces
  TLS 1.2, and swallows every failure (offline, GitHub rate-limiting, an API
  change), so it can't slow down or disrupt startup. Turn it off to stop the
  network request entirely.

Settings persist to `HKEY_LOCAL_MACHINE\Software\WgSharp` in the registry
(non-portable mode) or a `WgSharp.settings` file next to the executable
(portable mode only). Using the registry means the background service
(LocalSystem) reads the same values the GUI writes, upgrades never lose
settings, and the app leaves no stray files in Program Files or ProgramData.
Existing users upgrading from a file-based build are migrated automatically on
first launch — values are copied to the registry and the old file is removed.

## Tunnel list: import, drag-and-drop, double-click to connect

- **Import** via the toolbar accepts `.conf`, `.zip` (a batch of `.conf`
  files), or `.wgsp` (an encrypted portable export from another WgSharp
  install).
- **Drag and drop** any of those file types straight onto the tunnel list to
  import them the same way — including AmneziaWG-extended configs (see
  [AmneziaWG](#amneziawg-censorship-resistance-obfuscation)), since those are
  still plain `.conf` files as far as importing is concerned.
- **Drag a tunnel out** of the list onto Explorer, the desktop, or another
  app to export it as a `.conf` file. In portable mode this may prompt for
  that tunnel's password the first time (cached for the rest of the
  session, like every other action on it).
- **Right-click** a tunnel for a quick **Connect**/**Disconnect** (whichever
  applies) and **Remove**.
- **Drag to reorder** tunnels within the list; the new order is remembered
  across restarts. The same drag gesture used for reordering is also what
  exports a tunnel — dropping back onto the list reorders it, dropping
  anywhere else exports it, decided automatically by where it lands rather
  than anything you choose up front.
- **Double-click** a tunnel's name to activate it — or to deactivate it, if
  it's the one currently running.
- **Scan from QR code** (Add Tunnel menu) imports a tunnel by reading a QR
  code — either live via the webcam, or from a saved image. See
  [Scanning a QR code](#scanning-a-qr-code) for how it works and its limits.

## Scanning a QR code *(experimental)*

**Add Tunnel → Scan from QR code** is available when **Settings → Show
experimental features** is on. It opens a small live preview and decodes
whatever QR code the webcam sees — the same kind of code a tunnel's own
**Show QR** produces, or one exported from the official WireGuard mobile app.
A **Scan from image file…** button is always available alongside it, for a
screenshot or photo of a code instead of (or when) the webcam isn't usable.

Both the QR decoder and the webcam access are from-scratch — no external
library, matching the rest of the project:

- **Decoding** shares its GF(256)/Reed-Solomon/module-layout code with
  WgSharp's own QR *encoder* (used for **Show QR**), so the two stay in sync
  by construction. It handles the byte-mode payloads WgSharp's encoder (and
  most general QR generators) produce for arbitrary text, plus numeric and
  alphanumeric mode; ECI and Kanji mode are not supported.
- **Finding** the code in a frame assumes it's reasonably flat and facing the
  camera — in-plane rotation is fine, but there's no full perspective
  (keystone) correction, so try to hold the code roughly parallel to the
  webcam rather than at a steep angle.
- **Webcam access** uses the legacy Video for Windows capture API
  (`avicap32.dll`), which ships with Windows and needs no install — but isn't
  guaranteed to work with every camera. Most UVC webcams still expose the
  compatibility shim it needs; some newer or IR/Windows-Hello-only cameras
  don't. If no usable driver is found, the dialog says so and goes straight to
  the image-file option. The live preview is drawn by WgSharp itself from the
  camera's frame callback (not VFW's own preview rendering, which is
  unreliable across drivers), so what you see is exactly the frames being
  decoded.
- **Camera connects but you see nothing (black box, or no image at all)?**
  That's almost always Windows' camera privacy setting blocking desktop apps,
  not a WgSharp bug — on Windows 10/11, Settings → Privacy & security →
  Camera can block Win32 apps from getting image data even after the capture
  API reports a successful connection. The dialog detects both
  variants (all-black frames, *and* no frames arriving at all) automatically
  and shows an **Open camera privacy settings…** button that jumps straight
  to the right page.

## Portable mode and passwords

In portable mode, WgSharp stores everything — tunnel configs and settings — in
the app folder itself, writing nothing to the registry or ProgramData. This
makes the entire app self-contained: copy the folder to a USB drive and it
works on any machine without leaving a trace behind.

Each portable tunnel can have its own password. WgSharp caches a tunnel's
password in memory the first time you unlock it each session — whether that's
by activating, editing, saving, or exporting it — so you're asked **once per
tunnel**, not once per action. The cache lives only for the running session;
nothing is written to disk in plaintext.

Portable files carry a `WGSP` magic header (PBKDF2-HMAC-SHA256 + AES-256-CBC +
HMAC-SHA256, encrypt-then-MAC), so importing one into any WgSharp install is
recognized automatically and prompts for its password; importing into a
non-portable install decrypts it straight into the normal DPAPI store.

Because a portable copy installs no background service, it needs administrator
rights to create the adapter itself. Launched unelevated, it offers to
relaunch itself as administrator (or, if declined, closes — portable mode
needs elevation to do anything, so it doesn't leave a dead window open);
launched elevated, it runs the tunnel in-process and leaves no machine
footprint. Auto-start options (login GUI, background service) are also off and
greyed out in portable mode, since a password-encrypted portable tunnel has no
one present at boot/login to unlock it. See [Run](#run).

Portable mode is **determined by where WgSharp runs from**, not a free choice:
a copy in `Program Files` (an MSI install) is always non-portable, and a copy
anywhere else is always portable. The Settings checkbox reflects this as a
fixed, greyed indicator — checked for a standalone copy, unchecked for an
install — so it can't drift from what the app actually does.

## Architecture

```
src/
  Program.cs                  entry point, crash handler, mutex
  crypto/                     primitives (all verified against RFC vectors)
    Curve25519.cs               X25519 (RFC 7748)
    ChaCha20Poly1305.cs         AEAD (RFC 8439) + HChaCha20/XChaCha20-Poly1305
    CngChaCha20Poly1305.cs      CNG-accelerated per-session AEAD (BCryptEncrypt/Decrypt)
    Blake2s.cs                  hash + keyed MAC (RFC 7693)
    Kdf.cs                      HKDF over BLAKE2s (KDF1/2/3)
    Tai64N.cs                   handshake timestamp
  proto/
    Messages.cs                  wire constants + offsets (incl. cookie reply)
    Handshake.cs                  Noise IKpsk2 (initiator) + mac2/cookie
    Session.cs                    transport data encrypt/decrypt + counter
    ReplayWindow.cs                RFC 6479 sliding window
    Timers.cs                       rekey/keepalive constants
  net/
    UdpTransport.cs              UDP transport, endpoint (re-)resolution
  core/
    Config.cs                    .conf parser (multi-peer)
    ITunnelBackend.cs             common interface for the two backends
    Tunnel.cs                      managed backend: 3 pump threads + timers
    WireGuardNtTunnel.cs            WireGuardNT (kernel) backend
    AllowedIpRouter.cs             longest-prefix-match cryptokey routing
    PeerState.cs                   per-peer runtime state
    ConfigStore.cs                 DPAPI / portable-encrypted config storage
    ConfigWriter.cs                config save/delete, with service fallback for admin-owned files
    PortableCrypto.cs              AES-256-CBC + HMAC, encrypt-then-MAC
    AppSettings.cs                  persisted app settings (HKLM registry; file in portable mode)
    Elevation.cs                     elevation check + one-time "runas" self-launch
    UpdateChecker.cs                 background GitHub "newer release?" check
    InstallLocation.cs                installed-vs-portable detection + first-run defaults
    Logger.cs                        Log-tab/service verbosity gate (Debug log toggle)
    LoginAutostart.cs                 per-user "start GUI at login" Run-key entry
    ServiceProtocol.cs              GUI<->service named-pipe wire format (+ escaping)
    ServiceClient.cs                 GUI-side pipe client
    ServiceInstaller.cs              sc.exe wrapper (register auto-start manager / remove)
    ElevatedSetup.cs                 --elevated-setup: register+start service, firewall pre-auth
    ServiceState.cs                   persisted "last connected tunnel" (HKLM registry)
    RemoteTunnelBackend.cs            ITunnelBackend that drives the service over the pipe
  tun/
    Wintun.cs                     wintun.dll P/Invoke + managed wrapper
    WintunDownloader.cs             signed-download + SHA-256 verification
    WireGuardNtDownloader.cs         wireguard.dll download + Authenticode/publisher verification
    DriverBootstrap.cs              fetches both drivers at startup
    AdapterConfig.cs                address/route/DNS/MTU/interface-metric setup
    KillSwitch.cs                    kill-switch facade (delegates to WFP)
    WfpKillSwitch.cs                 WFP P/Invoke kill-switch implementation
    FirewallSelfRegister.cs          pre-authorizes the exe with Windows Firewall
  svc/                            background-service mode (same exe; see Program.cs)
    WgSharpService.cs              ServiceBase host + named-pipe manager server
    PipeClientAuth.cs               admin-gates pipe control commands (UAC linked-token check)
  ui/                             WinForms GUI (hand-written, no .resx)
```

## Cryptography: what's verified

Every primitive was cross-checked against reference implementations before
integration:

- **ChaCha20-Poly1305** — RFC 8439 §2.8.2 known-answer vector, plus random
  encryptions checked against a reference AEAD. At the transport layer,
  `Session` automatically uses Windows CNG (`BCryptEncrypt`/`BCryptDecrypt`
  with `BCRYPT_CHACHA20_POLY1305_ALGORITHM`) where available (Windows 10
  1709+), giving 3-6× higher throughput via SIMD-accelerated crypto. The
  managed `ChaCha20Poly1305` implementation remains as a fallback and as the
  path for the handshake (which doesn't need throughput). Both paths produce
  byte-for-byte identical wire output.
- **X25519** — both RFC 7748 §5.2 vectors, plus random keypairs including DH
  agreement.
- **HChaCha20 / XChaCha20-Poly1305** — verified against the RFC draft test
  vector and a reference roundtrip (used for cookie replies).
- **BLAKE2s** — plain and keyed, multiple lengths, vs. a reference hash
  library.
- **HMAC / KDF** — HMAC-BLAKE2s vs. reference; KDF chaining structure.
- **Handshake** — full initiator↔responder round-trip confirming both sides
  derive matching, correctly-swapped transport keys, including mac1/mac2.
- **Replay window** — randomized trials vs. a brute-force reference.
- **Transport framing** — type-4 round-trip, keepalive sizing, tamper
  rejection.
- **Key material lifetime** — private key and session key bytes are explicitly
  zeroed (`Array.Clear`) as soon as they're no longer needed: intermediate DH
  scalars (`es`, `ss`, `ee`, `se`) and temporary AEAD keys are zeroed
  immediately after each handshake step; session keys (`_sendKey`, `_recvKey`)
  are zeroed in `Session.Dispose()`; the static private key and PSK in the
  `Config` object are zeroed in `Tunnel.Stop()` / `WireGuardNtTunnel.Stop()`
  after all threads have exited and no rekey can occur. The plaintext config
  string returned by `ConfigStore.Load()` cannot be zeroed (managed strings
  are immutable and GC-movable), but the intermediate decrypted `byte[]` is
  zeroed before `string` conversion. The private key must remain in memory
  for the full tunnel lifetime to support rekeying — the zeroing only reduces
  the window after disconnection during which a memory dump would expose it.

## Current scope and limitations

**Implemented:** initiator handshake, multi-peer with cryptokey routing,
transport data both directions (with CNG-accelerated ChaCha20-Poly1305 on
Windows 10 1709+), replay protection, rekey + keepalive timers, optional PSK,
mac2/cookie replies, endpoint re-resolution, interface address assignment and
route installation (IP Helper API with netsh fallback, including the
`/1`-split full-tunnel-default trick), automatic driver downloads with
SHA-256 verification (Wintun) and Authenticode verification (WireGuardNT),
DPAPI- or password-encrypted config storage, a WFP-backed kill-switch,
self-registered Windows Firewall rules, drag-and-drop import/export/reorder,
double-click-to-connect, per-tunnel password caching, a syntax-highlighting
config editor with live-derived public key and QR export, split-tunnel DNS
auto-routing, AmneziaWG obfuscation on the managed backend, background
service with pre-login reconnect, auto-reconnect after MSI upgrade or
restart, explicit key material zeroing after disconnect, and a Stats tab
with live charts.

**Not yet implemented or hardened:**
- **Responder role** — WgSharp can only initiate handshakes (a client
  doesn't need to answer them).
- **IPv6 transport** — IPv6 peers/AllowedIPs are parsed and routes are
  installed, but the managed backend's crypto path is IPv4-only.

### Important caveats

- The crypto is **verified for correctness, not audited for side channels**.
  Constant-time intent exists (branch-free CSwap, tag comparison) but a
  managed JIT weakens those guarantees. Don't rely on this for adversarial use.
- **Key material in memory:** the private key must remain in memory while the
  tunnel is active (for rekeying). After disconnect, all key material is
  explicitly zeroed. A memory dump taken while the tunnel is running will
  expose the private key — this is unavoidable in any VPN client and applies
  equally to the official WireGuard client. The managed string returned by
  `ConfigStore.Load()` (which contains the plaintext config including the key)
  lingers until GC collects it and cannot be zeroed, but the window is brief.
- This is an independent implementation, written and tested incrementally
  against real hardware over many iterations — treat it as a serious hobby
  project, not a hardened production VPN client.

## Disclaimer

WgSharp is an independent, unofficial implementation of the WireGuard
protocol. It is not affiliated with, endorsed by, or supported by the
WireGuard project or Jason A. Donenfeld.

## License

WgSharp's own source code is licensed under the [MIT License](LICENSE).

WgSharp optionally uses the official, signed `wintun.dll` (Wintun) and
`wireguard.dll` (WireGuardNT) drivers, both by Jason A. Donenfeld / WireGuard
LLC. Those drivers are **not** part of WgSharp's own codebase — they're
downloaded separately at runtime — and their underlying driver source code is
licensed under the **GNU GPLv2**. The prebuilt, signed DLL binaries that
WgSharp actually downloads and uses are distributed under WireGuard LLC's own
"Prebuilt Binaries License," which permits shipping the unmodified DLL
alongside other software via its documented API. Full source for both
projects is available in the author's own repositories:

- Wintun: <https://git.zx2c4.com/wintun/> (mirror: <https://github.com/WireGuard/wintun>)
- WireGuardNT: <https://git.zx2c4.com/wireguard-nt/> (mirror: <https://github.com/WireGuard/wireguard-nt>)

## Acknowledgements

- **Jason A. Donenfeld** — designed the WireGuard protocol and reference
  implementation, and authored Wintun and WireGuardNT.
- **WireGuard** is a registered trademark of Jason A. Donenfeld.

## ❤️ Support My Work

If you find my projects useful, consider supporting me:

[![Buy Me A Coffee](https://img.buymeacoffee.com/button-api/?text=Buy+me+a+coffee&emoji=☕&slug=inteliboy&button_colour=FFDD00&font_colour=000000&font_family=Cookie&outline_colour=000000&coffee_colour=ffffff)](https://buymeacoffee.com/inteliboy)

---

<div align="center">

Copyright © 2026 [inteliboy](https://github.com/inteliboy) · Made with C# 5, `csc.exe`, and a healthy disregard for build systems.

</div>
