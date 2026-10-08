using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace WgSharp.Core
{
    /// <summary>
    /// Command-line control of the background service over the same named
    /// pipe the GUI uses: WgSharp.exe --up NAME | --down | --status | --list.
    /// Runs before the single-instance mutex, so it works while the GUI is open.
    ///
    /// WgSharp.exe is a GUI-subsystem exe, so a console shell does not wait
    /// for it; output is attached to the parent console (AttachConsole) but
    /// scripts that need the exit code should launch it with
    /// "start /wait" or "Start-Process -Wait -PassThru".
    ///
    /// Portable-mode tunnels are encrypted with a password only the GUI can
    /// ask for, so --up only handles the machine store.
    /// </summary>
    public static class Cli
    {
        [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        public const int ExitOk = 0;
        public const int ExitFailed = 1;
        public const int ExitUsage = 2;
        public const int ExitNoService = 3;
        public const int ExitDenied = 4;
        public const int ExitTimeout = 5;

        /// <summary>True if args[0] is one of the CLI verbs.</summary>
        public static bool IsCliCommand(string[] args)
        {
            if (args.Length == 0) return false;
            switch (args[0].ToLowerInvariant())
            {
                case "--up": case "--down": case "--status": case "--list": case "--help": case "-h": case "/?":
                    return true;
            }
            return false;
        }

        public static int Run(string[] args)
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            string verb = args[0].ToLowerInvariant();
            try
            {
                switch (verb)
                {
                    case "--up": return Up(args);
                    case "--down": return Down();
                    case "--status": return Status();
                    case "--list": return List();
                    default: Usage(); return ExitOk;
                }
            }
            finally { Console.Out.Flush(); }
        }

        private static void Usage()
        {
            Console.WriteLine("WgSharp command line (requires the background service)");
            Console.WriteLine("  WgSharp.exe --up <tunnel> [--no-wait]   activate a tunnel");
            Console.WriteLine("  WgSharp.exe --down                      deactivate the active tunnel");
            Console.WriteLine("  WgSharp.exe --status                    print the active tunnel's state");
            Console.WriteLine("  WgSharp.exe --list                      list saved tunnels");
            Console.WriteLine("Exit codes: 0 ok, 1 failed, 2 usage, 3 service not running, 4 denied, 5 timeout");
            Console.WriteLine("From a console use 'start /wait WgSharp.exe ...' so the shell waits for the exit code.");
        }

        private static int Up(string[] args)
        {
            string name = null;
            bool wait = true;
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--no-wait", StringComparison.OrdinalIgnoreCase)) wait = false;
                else if (name == null) name = args[i];
            }
            if (string.IsNullOrEmpty(name)) { Usage(); return ExitUsage; }
            if (name.IndexOf('|') >= 0) { Console.Error.WriteLine("Invalid tunnel name."); return ExitUsage; }

            if (!ServiceClient.IsServiceRunning())
            { Console.Error.WriteLine("The WgSharp service is not running."); return ExitNoService; }

            string resp = ServiceClient.SendCommand("ACTIVATE|" + name);
            int rc = CheckControlReply(resp);
            if (rc != ExitOk) return rc;
            if (!wait) { Console.WriteLine("Activating " + name + "..."); return ExitOk; }

            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            string lastState = null;
            while (DateTime.UtcNow < deadline)
            {
                string n;
                TunnelStatus s = ServiceProtocol.ParseStatus(ServiceClient.SendCommand("STATUS"), out n);
                if (s != null && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (s.State != lastState) { lastState = s.State; Console.WriteLine(name + ": " + s.State); }
                    if (s.State == "Connected") return ExitOk;
                    if (s.State == "Failed") { Console.Error.WriteLine("Activation failed."); return ExitFailed; }
                }
                Thread.Sleep(500);
            }
            Console.Error.WriteLine("Timed out waiting for " + name + " to connect.");
            return ExitTimeout;
        }

        private static int Down()
        {
            if (!ServiceClient.IsServiceRunning())
            { Console.Error.WriteLine("The WgSharp service is not running."); return ExitNoService; }
            int rc = CheckControlReply(ServiceClient.SendCommand("DEACTIVATE"));
            if (rc == ExitOk) Console.WriteLine("Deactivated.");
            return rc;
        }

        private static int Status()
        {
            if (!ServiceClient.IsServiceRunning())
            { Console.Error.WriteLine("The WgSharp service is not running."); return ExitNoService; }
            string name;
            TunnelStatus s = ServiceProtocol.ParseStatus(ServiceClient.SendCommand("STATUS"), out name);
            if (s == null) { Console.WriteLine("INACTIVE"); return ExitOk; }
            Console.WriteLine("Tunnel:    " + name);
            Console.WriteLine("State:     " + s.State);
            Console.WriteLine("Endpoint:  " + s.Endpoint);
            Console.WriteLine("Handshake: " + (s.LastHandshakeTime == DateTime.MinValue
                ? "never" : (int)(DateTime.Now - s.LastHandshakeTime).TotalSeconds + "s ago"));
            Console.WriteLine("Received:  " + s.RxBytes + " bytes");
            Console.WriteLine("Sent:      " + s.TxBytes + " bytes");
            if (s.LatencyMs >= 0) Console.WriteLine("Latency:   " + s.LatencyMs + " ms");
            return s.State == "Connected" ? ExitOk : ExitFailed;
        }

        private static int List()
        {
            foreach (string n in ConfigStore.List()) Console.WriteLine(n);
            return ExitOk;
        }

        private static int CheckControlReply(string resp)
        {
            if (resp == null) { Console.Error.WriteLine("No reply from the service."); return ExitNoService; }
            if (resp == "OK") return ExitOk;
            if (resp == "DENIED")
            { Console.Error.WriteLine("Denied: the service only accepts control commands from Administrators."); return ExitDenied; }
            if (resp.StartsWith("ERR|", StringComparison.Ordinal)) resp = resp.Substring(4);
            Console.Error.WriteLine("Error: " + resp);
            return ExitFailed;
        }
    }
}
