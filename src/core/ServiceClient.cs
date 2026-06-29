using System;
using System.IO;
using System.IO.Pipes;

namespace WgSharp.Core
{
    /// <summary>
    /// The GUI side of the named-pipe IPC with WgSharpSvc. Every call is a
    /// short-lived connection: connect, send one line, read one line,
    /// disconnect — simple and fine for a handful of low-frequency commands
    /// (the status poll, and the rare activate/deactivate click).
    /// </summary>
    public static class ServiceClient
    {
        private const int ConnectTimeoutMs = 800;

        /// <summary>
        /// Cheap liveness check: a connect attempt with a short timeout. This
        /// is also how "is the background service installed and running" is
        /// determined elsewhere — if nothing is listening on the pipe, the
        /// service either isn't installed or isn't running, and either way
        /// the GUI falls back to running tunnels in-process as it always has.
        /// </summary>
        public static bool IsServiceRunning()
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", ServiceProtocol.PipeName, PipeDirection.InOut))
                {
                    pipe.Connect(ConnectTimeoutMs);
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Sends one line, returns the one-line response, or null on any failure/timeout.</summary>
        public static string SendCommand(string command)
        {
            string err;
            return SendCommand(command, out err);
        }

        /// <summary>
        /// Same as SendCommand, but also reports why it failed (connect vs read
        /// vs permission) via the err out-param — null err means success. Used
        /// for diagnostics when the GUI can't read status from the service.
        /// </summary>
        public static string SendCommand(string command, out string err)
        {
            err = null;
            try
            {
                using (var pipe = new NamedPipeClientStream(".", ServiceProtocol.PipeName, PipeDirection.InOut))
                {
                    try { pipe.Connect(ConnectTimeoutMs); }
                    catch (Exception ce) { err = "connect: " + ce.GetType().Name + ": " + ce.Message; return null; }

                    pipe.ReadMode = PipeTransmissionMode.Byte;

                    // IMPORTANT: do NOT wrap the writer/reader in `using`.
                    // Disposing a StreamWriter/StreamReader also closes the
                    // underlying stream (the pipe) — so the first dispose
                    // closes the pipe, and the second (plus the outer pipe
                    // `using`) then throws ObjectDisposedException ("cannot
                    // access a closed pipe"). That exception fired AFTER
                    // ReadLine() had already produced the response, replacing
                    // the real value with null on the way out — which is
                    // exactly why the GUI saw <null> and got stuck showing
                    // "negotiating" even though the service answered fine.
                    // We let the single outer `using (pipe)` own disposal;
                    // flushing the writer is all that's needed before reading.
                    var writer = new StreamWriter(pipe);
                    writer.AutoFlush = false;
                    var reader = new StreamReader(pipe);

                    writer.WriteLine(command);
                    writer.Flush();

                    string line = reader.ReadLine();
                    if (line == null) err = "read: got null (server closed without replying)";
                    return line;
                }
            }
            catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; return null; }
        }

        /// <summary>
        /// Pulls the service's recent in-memory log (the LOG command) and
        /// returns it as individual lines, oldest-first, or an empty array if
        /// the service isn't reachable or has nothing. Lets the GUI surface
        /// service activity in its Log tab without any file on disk.
        /// </summary>
        public static string[] FetchServiceLog()
        {
            string resp = SendCommand("LOG");
            if (string.IsNullOrEmpty(resp) || !resp.StartsWith("LOG|", StringComparison.Ordinal))
                return new string[0];
            string payload = resp.Substring(4);
            if (payload.Length == 0) return new string[0];
            // Reverse the escaping done service-side (\\ and \n).
            var sb = new System.Text.StringBuilder(payload.Length);
            for (int i = 0; i < payload.Length; i++)
            {
                char c = payload[i];
                if (c == '\\' && i + 1 < payload.Length)
                {
                    char n = payload[++i];
                    sb.Append(n == 'n' ? '\n' : n);
                }
                else sb.Append(c);
            }
            return sb.ToString().Split('\n');
        }
    }
}
