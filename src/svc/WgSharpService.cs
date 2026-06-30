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
    /// The background tunnel service, modeled on the official WireGuard for
    /// Windows "tunnel service" (wireguard.exe /tunnelservice). The crucial
    /// design point, learned from that implementation: activating a tunnel is
    /// STARTING this service, and deactivating is STOPPING it — the slow
    /// (~1s) adapter/route/driver bring-up IS this service's own OnStart, and
    /// the Service Control Manager owns that long-running operation, with its
    /// own start/stop timeouts and state machine.
    ///
    /// An earlier version of WgSharp tried to drive activation as a command
    /// sent over a named pipe, which held the pipe open for the entire
    /// bring-up; the client gave up waiting, and the resulting teardown stopped
    /// the tunnel that had just come up. SCM is the right tool for a multi-
    /// second start, so we let it do exactly what it does for the official
    /// client: start the process, let Execute/OnStart bring the tunnel up, and
    /// report SERVICE_RUNNING once it's up.
    ///
    /// Which tunnel to run is read from ServiceState (persisted, machine-wide)
    /// — set by the GUI just before it asks SCM to start the service. Only
    /// non-portable (machine-DPAPI) tunnels work here; portable tunnels are
    /// password-encrypted and there's no human at a pre-login service.
    /// </summary>
    public sealed class WgSharpService : ServiceBase
    {
        private ITunnelBackend _tunnel;
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
            // Lightweight STATUS-only pipe server. Unlike the earlier broken
            // design, this NEVER does slow work on a pipe request — activation
            // is driven by SCM (this very OnStart), not by a pipe command — so
            // the pipe only ever answers quick STATUS/PING queries and can't
            // hang the client.
            _pipeThread = new Thread(PipeServerLoop) { IsBackground = true, Name = "wgsharpsvc-pipe" };
            _pipeThread.Start();

            // OnStart must return promptly-ish; the actual bring-up runs on a
            // worker thread and we let SCM see us as started. (The official
            // client similarly does its heavy lifting inside the service's
            // Execute and relies on SCM's start-pending window.) Bringing up
            // the adapter here, in the service's own start, is the whole point
            // — this is the long-running operation SCM is built to manage.
            string name = ServiceState.GetLastTunnel();
            LogLine("Service starting" + (string.IsNullOrEmpty(name) ? " (no tunnel set)." : " for tunnel '" + name + "'."));

            if (string.IsNullOrEmpty(name))
            {
                // Nothing to do; a bare start with no tunnel selected just idles.
                // (Shouldn't normally happen — the GUI sets the tunnel before
                // starting us — but we must not crash if it does.)
                return;
            }

            if (AppSettings.PortableMode)
            {
                LogLine("Portable mode is on; the service can't use password-encrypted tunnels. Stopping.");
                // Signal failure so SCM/the GUI sees this didn't take.
                ExitCode = 1;
                Stop();
                return;
            }

            Thread t = new Thread(delegate ()
            {
                try { ActivateInternal(name); LogLine("Tunnel '" + name + "' is up."); }
                catch (Exception ex)
                {
                    LogLine("Activation failed: " + ex.Message);
                    // Surface the failure as a service-specific exit so the
                    // GUI (watching SCM state) can tell it didn't come up.
                    ExitCode = 1;
                    try { Stop(); } catch { }
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
            // store — it cannot decrypt portable tunnels without a human.
            bool wasPortable = AppSettings.PortableMode;
            AppSettings.PortableMode = false;
            string text;
            try { text = ConfigStore.Load(name); }
            finally { AppSettings.PortableMode = wasPortable; }

            Config cfg = Config.Parse(text);

            lock (_lock)
            {
                if (_tunnel != null)
                {
                    try { _tunnel.Stop(); } catch (Exception ex) { LogLine("Stop (pre-switch) error: " + ex.Message); }
                    _tunnel = null;
                }
                ITunnelBackend tunnel = AppSettings.UseWireGuardNt
                    ? (ITunnelBackend)new WgSharp.Core.WireGuardNtTunnel(cfg)
                    : new WgSharp.Core.Tunnel(cfg);
                tunnel.LogMessage += LogLine;
                tunnel.Start();
                _tunnel = tunnel;
            }
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

            string reply;
            if (line == "PING")
            {
                reply = "PONG";
            }
            else if (line == "STATUS")
            {
                lock (_lock)
                {
                    reply = _tunnel == null
                        ? "INACTIVE"
                        : ServiceProtocol.FormatStatus(ServiceState.GetLastTunnel(), _tunnel.GetStatus());
                }
                LogLine(Logger.DebugMarker + "STATUS query answered: " + reply);
            }
            else if (line == "LOG")
            {
                // Hand the GUI the recent in-memory service log so it can show
                // service activity in its Log tab without any file on disk.
                // Encoded as one line: backslashes and newlines are escaped so
                // the whole snapshot travels as a single pipe message.
                string snap = DrainLogSnapshot();
                reply = "LOG|" + snap.Replace("\\", "\\\\").Replace("\n", "\\n");
            }
            else
            {
                reply = "ERROR:unsupported";
            }

            writer.WriteLine(reply);
            writer.Flush();
            try { server.WaitForPipeDrain(); } catch { } // ensure the client actually got it before we close
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
