using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using WgSharp.Core;

namespace WgSharp.Svc
{
    /// <summary>
    /// The background tunnel MANAGER service. It runs continuously (installed
    /// start=auto), idling when no tunnel is active, and the GUI drives it
    /// entirely over the named pipe — ACTIVATE / DEACTIVATE / SAVECONFIG /
    /// DELETECONFIG — so the GUI itself can run unelevated (asInvoker, no UAC
    /// prompt at launch). Control commands are restricted to Administrators-
    /// group members, verified against the client's UAC LINKED token so an
    /// admin's unelevated GUI passes without any prompt (see PipeClientAuth).
    ///
    /// The earlier design made activation an SCM start of this service and
    /// deactivation an SCM stop — which forced the GUI to hold SCM rights,
    /// i.e. to be elevated, i.e. a UAC prompt on every launch. Before that,
    /// an even earlier design DID use a pipe ACTIVATE command but held the
    /// pipe open for the entire ~1s bring-up, which timed the client out and
    /// tore down the tunnel it had just started. This version keeps the pipe
    /// command but bakes in that lesson: a control command only VALIDATES
    /// synchronously and replies instantly ("OK"/"ERR|..."); the actual
    /// bring-up/teardown runs on a worker thread, serialized by _lock, and
    /// the GUI follows progress the way it always has — polling STATUS and
    /// pumping LOG.
    ///
    /// Boot reconnect: ServiceState (HKLM, written by THIS service now, never
    /// by the GUI) remembers the last successfully requested tunnel; OnStart
    /// re-activates it. Deactivation clears it. Portable (password-encrypted)
    /// tunnels are activated via ACTIVATE2 with the decrypted config supplied
    /// by the GUI and are never persisted — there is no human at boot to type
    /// a password.
    /// </summary>
    public sealed class WgSharpService : ServiceBase
    {
        private ITunnelBackend _tunnel;
        private string _activeName;              // guarded by _lock
        private readonly object _lock = new object();
        private readonly object _logLock = new object();
        private Thread _pipeThread;
        private volatile bool _stopping;
        private NamedPipeServerStream _activeListener;

        public WgSharpService()
        {
            ServiceName = "WgSharpSvc";
            CanStop = true;
            CanShutdown = true;
        }

        protected override void OnStart(string[] args)
        {
            _stopping = false;
            // Pipe server: quick queries for everyone, control commands for
            // admins. Command handling NEVER does slow work on the pipe (see
            // class comment) so a request can't hang the client.
            _pipeThread = new Thread(PipeServerLoop) { IsBackground = true, Name = "wgsharpsvc-pipe" };
            _pipeThread.Start();

            // Driver bootstrap moved here from the GUI: the asInvoker GUI can
            // no longer write wintun.dll / wireguard.dll next to the exe in
            // Program Files, but this LocalSystem service can. Async and
            // best-effort, exactly as it was in the GUI.
            try
            {
                WgSharp.Tun.DriverBootstrap.Log += LogLine;
                WgSharp.Tun.DriverBootstrap.EnsureDriversAsync();
            }
            catch (Exception ex) { LogLine(Logger.DebugMarker + "Driver bootstrap error: " + ex.Message); }

            // Boot / restart reconnect: if a tunnel was active when we last
            // ran, bring it back up. No tunnel set (the normal always-on idle
            // case) just idles — the service stays up waiting for pipe
            // commands, it never stops itself anymore.
            string name = ServiceState.GetLastTunnel();
            LogLine("Service starting" + (string.IsNullOrEmpty(name) ? " (idle; no tunnel to reconnect)." : "; reconnecting tunnel '" + name + "'."));
            if (!string.IsNullOrEmpty(name))
                StartActivationWorker(name, null);
        }

        /// <summary>
        /// Runs the multi-second bring-up on a worker thread — the pipe (or
        /// OnStart) has already replied by the time this runs. configText is
        /// null for named (machine-store) tunnels and the decrypted config for
        /// portable ACTIVATE2 activations. Failure logs, clears the persisted
        /// reconnect state, and leaves the service running and idle.
        /// </summary>
        private void StartActivationWorker(string name, string configText)
        {
            Thread t = new Thread(delegate ()
            {
                try
                {
                    if (configText == null) ActivateInternal(name);
                    else ActivateParsed(name, Config.Parse(configText));
                    LogLine("Tunnel '" + name + "' is up.");
                }
                catch (Exception ex)
                {
                    LogLine("Activation failed: " + ex.Message);
                    ServiceState.Clear();
                    lock (_lock) { _activeName = null; }
                }
            });
            t.IsBackground = true;
            t.Name = "wgsharpsvc-activate";
            t.Start();
        }

        protected override void OnStop()
        {
            LogLine("Service stopping.");
            _stopping = true;
            try { var l = _activeListener; if (l != null) l.Dispose(); } catch { }
            StopTunnelInternal();
            try { if (_pipeThread != null) _pipeThread.Join(2000); } catch { }
        }

        protected override void OnShutdown()
        {
            OnStop();
        }

        private void ActivateInternal(string name)
        {
            // The service only ever operates on the machine (non-portable)
            // store — it cannot decrypt portable tunnels without a human
            // (those arrive pre-decrypted via ACTIVATE2 -> ActivateParsed).
            ActivateParsed(name, Config.Parse(LoadMachineConfig(name)));
        }

        /// <summary>Reads a named config from the machine store regardless of the portable-mode setting.</summary>
        private static string LoadMachineConfig(string name)
        {
            bool wasPortable = AppSettings.PortableMode;
            AppSettings.PortableMode = false;
            try { return ConfigStore.Load(name); }
            finally { AppSettings.PortableMode = wasPortable; }
        }

        private void ActivateParsed(string name, Config cfg)
        {
            lock (_lock)
            {
                if (_tunnel != null)
                {
                    try { _tunnel.Stop(); } catch (Exception ex) { LogLine("Stop (pre-switch) error: " + ex.Message); }
                    _tunnel = null;
                    _activeName = null;
                }

                bool wantNt = AppSettings.UseWireGuardNt &&
                              !WgSharp.Core.TunnelBackendFactory.RequiresManagedBackend(cfg);

                if (wantNt)
                    LogLine("WireGuardNT backend selected.");

                ITunnelBackend tunnel = null;
                if (wantNt)
                {
                    try
                    {
                        tunnel = new WireGuardNtTunnel(cfg);
                        tunnel.LogMessage += LogLine;
                        tunnel.Start();
                    }
                    catch (Exception ex)
                    {
                        LogLine("WireGuardNT failed (" + ex.Message + "); falling back to managed backend.");
                        // Write to a file too, so this is visible even if the pipe
                        // isn't reachable yet (e.g. early startup crash on ARM64).
                        WriteEmergencyLog("WireGuardNT fallback: " + ex.Message);
                        if (tunnel != null) { try { tunnel.Stop(); } catch { } }
                        tunnel = null;
                        wantNt = false;
                    }
                }

                if (tunnel == null)
                {
                    if (!wantNt)
                        LogLine("Using managed backend.");
                    tunnel = new Tunnel(cfg);
                    tunnel.LogMessage += LogLine;
                    tunnel.Start();
                }

                _tunnel = tunnel;
                _activeName = name;
            }
        }

        private static void WriteEmergencyLog(string message)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "WgSharp");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "service-error.log");
                System.IO.File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + "\r\n");
            }
            catch { }
        }

        private void StopTunnelInternal()
        {
            lock (_lock)
            {
                if (_tunnel != null)
                {
                    try { _tunnel.Stop(); }
                    catch (Exception ex) { LogLine("Stop error: " + ex.Message); }
                    _tunnel = null;
                }
            }
        }

        // ---------------- STATUS pipe server ----------------
        // The ONLY pipe commands are PING and STATUS — both return instantly.
        // Activation/deactivation are NOT pipe commands (that was the old
        // broken design); they're SCM start/stop of this service. So the pipe
        // can never be held open by slow work, and the cross-session ACL below
        // lets the user's GUI (a different session from this LocalSystem
        // service) actually connect.

        private void PipeServerLoop()
        {
            while (!_stopping)
            {
                NamedPipeServerStream server = null;
                try
                {
                    var security = new PipeSecurity();
                    security.AddAccessRule(new PipeAccessRule(
                        new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                        PipeAccessRights.ReadWrite, AccessControlType.Allow));
                    security.AddAccessRule(new PipeAccessRule(
                        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                        PipeAccessRights.FullControl, AccessControlType.Allow));

                    server = new NamedPipeServerStream(
                        ServiceProtocol.PipeName, PipeDirection.InOut, 4,
                        PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, security);
                    _activeListener = server;
                    server.WaitForConnection();
                    if (_stopping) { server.Dispose(); break; }

                    NamedPipeServerStream client = server;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try { HandleClient(client); }
                        catch (Exception ex) { LogLine("Client error: " + ex.Message); }
                        finally { try { client.Dispose(); } catch { } }
                    });
                }
                catch (Exception ex)
                {
                    if (server != null) { try { server.Dispose(); } catch { } }
                    if (!_stopping) { LogLine("Pipe server error: " + ex.Message); Thread.Sleep(500); }
                }
            }
        }

        private void HandleClient(NamedPipeServerStream server)
        {
            // Note: do NOT wrap `server` in a using here — PipeServerLoop's
            // worker already disposes it in a finally. We also avoid disposing
            // the StreamWriter/StreamReader (which would close the underlying
            // pipe early); instead we flush explicitly and WaitForPipeDrain so
            // the full response reaches the client before the pipe is torn
            // down. Truncation here was a likely cause of the client seeing a
            // null/short response and falling back to "negotiating".
            var reader = new StreamReader(server);
            var writer = new StreamWriter(server);
            writer.AutoFlush = false;

            string line = reader.ReadLine();
            if (line == null) return;

            // Split off the command verb; the remainder (if any) is parsed
            // per-command, splitting on at most one more '|' so escaped config
            // payloads pass through untouched.
            int sep = line.IndexOf('|');
            string verb = sep < 0 ? line : line.Substring(0, sep);
            string rest = sep < 0 ? null : line.Substring(sep + 1);

            string reply;
            if (verb == "PING")
            {
                reply = "PONG";
            }
            else if (verb == "STATUS")
            {
                lock (_lock)
                {
                    reply = _tunnel == null
                        ? "INACTIVE"
                        : ServiceProtocol.FormatStatus(_activeName ?? ServiceState.GetLastTunnel(), _tunnel.GetStatus());
                }
                LogLine(Logger.DebugMarker + "STATUS query answered: " + reply);
            }
            else if (verb == "LOG")
            {
                // Hand the GUI the recent in-memory service log so it can show
                // service activity in its Log tab without any file on disk.
                string snap = DrainLogSnapshot();
                reply = "LOG|" + ServiceProtocol.Escape(snap);
            }
            else if (verb == "ACTIVATE" || verb == "ACTIVATE2" || verb == "DEACTIVATE" ||
                     verb == "SAVECONFIG" || verb == "DELETECONFIG")
            {
                // CONTROL commands: the client's user must be an
                // Administrators member (directly, or via the UAC linked
                // token — which is how the unelevated GUI of an admin
                // qualifies without any prompt). Standard users get DENIED.
                if (!PipeClientAuth.IsAdminClient(server))
                {
                    LogLine("Denied control command from a non-administrator client: " + verb);
                    reply = "DENIED";
                }
                else
                {
                    reply = HandleControl(verb, rest);
                }
            }
            else
            {
                reply = "ERROR:unsupported";
            }

            writer.WriteLine(reply);
            writer.Flush();
            try { server.WaitForPipeDrain(); } catch { } // ensure the client actually got it before we close
        }

        /// <summary>
        /// Executes an authorized control command. Everything here follows one
        /// rule: VALIDATE synchronously (fast: parse a name, read + decrypt +
        /// parse a config file), reply immediately, and push any slow work
        /// (adapter bring-up, teardown) onto a worker thread. That keeps every
        /// pipe request instant, which is the fix for the historical
        /// held-open-pipe activation bug described in the class comment.
        /// </summary>
        private string HandleControl(string verb, string rest)
        {
            try
            {
                if (verb == "DEACTIVATE")
                {
                    LogLine("Deactivation requested.");
                    // Don't reconnect this at the next boot.
                    ServiceState.Clear();
                    Thread t = new Thread(delegate ()
                    {
                        StopTunnelInternal();
                        lock (_lock) { _activeName = null; }
                        LogLine("Tunnel deactivated; service idle.");
                    });
                    t.IsBackground = true;
                    t.Name = "wgsharpsvc-deactivate";
                    t.Start();
                    return "OK";
                }

                if (verb == "ACTIVATE")
                {
                    string name = rest;
                    if (string.IsNullOrEmpty(name)) return "ERR|No tunnel name given.";
                    // Validate NOW so the caller gets a real error instead of
                    // an OK followed by a silent failure: the config must
                    // exist in the machine store, decrypt, and parse.
                    try { Config.Parse(LoadMachineConfig(name)); }
                    catch (Exception ex) { return "ERR|" + ServiceProtocol.Escape("Config '" + name + "' could not be loaded: " + ex.Message); }
                    LogLine("Activation requested for tunnel '" + name + "'.");
                    // Set-then-activate, exactly the old SCM flow's ordering:
                    // persist the reconnect intent before bring-up; the worker
                    // clears it again if bring-up fails.
                    ServiceState.SetLastTunnel(name);
                    StartActivationWorker(name, null);
                    return "OK";
                }

                if (verb == "ACTIVATE2")
                {
                    // Portable tunnel: the GUI decrypted it with the user's
                    // password and sends the plaintext config over the pipe
                    // (local, ACL'd). Never persisted — no password at boot.
                    if (rest == null) return "ERR|Malformed ACTIVATE2.";
                    int p = rest.IndexOf('|');
                    if (p <= 0 || p == rest.Length - 1) return "ERR|Malformed ACTIVATE2.";
                    string name = rest.Substring(0, p);
                    string text = ServiceProtocol.Unescape(rest.Substring(p + 1));
                    try { Config.Parse(text); }
                    catch (Exception ex) { return "ERR|" + ServiceProtocol.Escape("Config did not parse: " + ex.Message); }
                    LogLine("Activation requested for portable tunnel '" + name + "' (config supplied by the GUI; not persisted).");
                    ServiceState.Clear();
                    StartActivationWorker(name, text);
                    return "OK";
                }

                if (verb == "SAVECONFIG")
                {
                    // Machine-store write performed as SYSTEM. This is what
                    // lets the unelevated GUI keep editing tunnels that were
                    // created by older elevated versions (whose files in
                    // ProgramData are owned by Administrators and reject an
                    // unelevated same-user write).
                    if (rest == null) return "ERR|Malformed SAVECONFIG.";
                    int p = rest.IndexOf('|');
                    if (p <= 0) return "ERR|Malformed SAVECONFIG.";
                    string name = rest.Substring(0, p);
                    string text = ServiceProtocol.Unescape(rest.Substring(p + 1));
                    try { Config.Parse(text); } // don't persist garbage as SYSTEM
                    catch (Exception ex) { return "ERR|" + ServiceProtocol.Escape("Config did not parse: " + ex.Message); }
                    bool wasPortable = AppSettings.PortableMode;
                    AppSettings.PortableMode = false;
                    try { ConfigStore.Save(name, text); }
                    finally { AppSettings.PortableMode = wasPortable; }
                    LogLine("Config '" + name + "' saved to the machine store.");
                    return "OK";
                }

                if (verb == "DELETECONFIG")
                {
                    string name = rest;
                    if (string.IsNullOrEmpty(name)) return "ERR|No tunnel name given.";
                    bool wasPortable = AppSettings.PortableMode;
                    AppSettings.PortableMode = false;
                    try { ConfigStore.Delete(name); }
                    finally { AppSettings.PortableMode = wasPortable; }
                    LogLine("Config '" + name + "' deleted from the machine store.");
                    return "OK";
                }

                return "ERROR:unsupported";
            }
            catch (Exception ex)
            {
                return "ERR|" + ServiceProtocol.Escape(ex.Message);
            }
        }

        // ---------------- logging ----------------
        // The service has no GUI of its own, so its log has two sinks:
        //
        //  1. An in-memory ring buffer (always on, tiny, costs no disk I/O).
        //     The GUI pulls recent meaningful lines from it over the pipe (the
        //     LOG command) and shows them in its Log tab, so service activity
        //     is visible without any file on disk.
        //
        //  2. A service.log file — written ONLY when Debug log is enabled in
        //     Settings. With Debug off (the default) nothing is written to
        //     disk at all, so the service never grows a file or wears the SSD
        //     with constant appends. Verbose "[dbg]"-marked lines are dropped
        //     entirely unless Debug is on, mirroring the GUI's Log tab filter.
        //
        // Logging must never be the reason the service crashes — everything
        // here is best-effort and swallows its own errors.

        // In-memory log ring buffer. The GUI pulls recent lines from this over
        // the pipe (the LOG command) and appends them to its own Log tab, so
        // the service never needs to show more than what happened "recently".
        // Each line is a timestamp + component tag + message, roughly 80-150
        // chars. 200 lines ≈ 20-30 KB -- small and tight. The GUI's own Log
        // tab has its own larger cap (LogMaxLines = 2000) and is the right
        // place for the user to scroll through history; the ring is just the
        // transfer buffer between service and GUI.
        private const int LogRingMax = 200;
        private readonly System.Collections.Generic.Queue<string> _logRing =
            new System.Collections.Generic.Queue<string>();

        private void LogLine(string message)
        {
            if (message == null) return;

            // Verbosity filter: drop debug-marked lines unless Debug log is on.
            if (!Logger.ShouldShow(message)) return;
            string display = Logger.Strip(message);
            string stamped = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + display;

            // (1) ring buffer — always.
            lock (_logLock)
            {
                _logRing.Enqueue(stamped);
                while (_logRing.Count > LogRingMax) _logRing.Dequeue();
            }

            // (2) file — only when Debug log is enabled.
            if (!AppSettings.DebugLog) return;
            try
            {
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string dir = Path.Combine(programData, "WgSharp");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "service.log");
                lock (_logLock)
                {
                    File.AppendAllText(path, stamped + Environment.NewLine);
                }
            }
            catch { }
        }

        /// <summary>Snapshot of the ring buffer, oldest-first, joined by newlines.</summary>
        private string DrainLogSnapshot()
        {
            lock (_logLock)
            {
                return string.Join("\n", _logRing.ToArray());
            }
        }
    }
}
