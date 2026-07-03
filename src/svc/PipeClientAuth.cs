using System;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WgSharp.Svc
{
    /// <summary>
    /// Decides whether a connected named-pipe client may issue CONTROL
    /// commands (ACTIVATE/DEACTIVATE/SAVECONFIG/...). Read-only commands
    /// (PING/STATUS/LOG) are open to any authenticated user, unchanged.
    ///
    /// The rule: the client's USER must be a member of BUILTIN\Administrators.
    /// The subtlety is UAC: an admin's unelevated process (which is exactly
    /// what the asInvoker GUI is) carries a FILTERED token where the
    /// Administrators SID is present but deny-only, so a naive
    /// WindowsPrincipal.IsInRole check fails for the very clients this exists
    /// to allow. The fix — the same one the official WireGuard client's
    /// manager service uses — is to also check the token's LINKED token (the
    /// unfiltered, elevated twin Windows keeps for every UAC-split logon):
    ///
    ///   1. Impersonate the pipe client (RunAsClient).
    ///   2. If the impersonation token itself is in Administrators — the
    ///      client is elevated, or UAC is off, or it's LocalSystem — allow.
    ///   3. Otherwise query TokenLinkedToken and check Administrators
    ///      membership on THAT. An unelevated admin passes here, with no UAC
    ///      prompt anywhere; a standard user has no admin linked token and is
    ///      denied.
    ///
    /// Security note: this authorizes the USER, not the process's elevation
    /// state — deliberately. Elevation exists to protect admins from silently
    /// exercising their own power; here the user explicitly clicks
    /// Activate/Deactivate in a VPN app they installed, and gating that on a
    /// per-click UAC prompt is precisely the annoyance this architecture
    /// removes. A standard user still cannot control the tunnel at all.
    /// </summary>
    internal static class PipeClientAuth
    {
        private const int TokenLinkedTokenClass = 19; // TOKEN_INFORMATION_CLASS.TokenLinkedToken

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(
            IntPtr tokenHandle, int tokenInformationClass,
            IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>
        /// True if the connected client's user is an Administrators member
        /// (directly or via the UAC linked token). Fails CLOSED: any error
        /// anywhere returns false.
        /// </summary>
        public static bool IsAdminClient(NamedPipeServerStream server)
        {
            bool allowed = false;
            try
            {
                server.RunAsClient(delegate
                {
                    try
                    {
                        // ifImpersonating:true — we want the client's token,
                        // never this service's own SYSTEM token by mistake.
                        using (WindowsIdentity id = WindowsIdentity.GetCurrent(true))
                        {
                            if (id == null) return;

                            if (new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
                            {
                                allowed = true; // elevated admin / UAC off / SYSTEM
                                return;
                            }

                            // Filtered token: look at the linked (elevated) twin.
                            allowed = LinkedTokenIsAdmin(id.Token);
                        }
                    }
                    catch { allowed = false; }
                });
            }
            catch { allowed = false; }
            return allowed;
        }

        private static bool LinkedTokenIsAdmin(IntPtr token)
        {
            // TOKEN_LINKED_TOKEN is a struct holding exactly one HANDLE.
            int size = IntPtr.Size;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                int returned;
                if (!GetTokenInformation(token, TokenLinkedTokenClass, buf, size, out returned))
                    return false; // no linked token: not a UAC-split logon (i.e. standard user)

                IntPtr linked = Marshal.ReadIntPtr(buf);
                if (linked == IntPtr.Zero) return false;
                try
                {
                    // WindowsIdentity duplicates the handle; we close ours below.
                    using (var lid = new WindowsIdentity(linked))
                        return new WindowsPrincipal(lid).IsInRole(WindowsBuiltInRole.Administrator);
                }
                finally
                {
                    CloseHandle(linked);
                }
            }
            catch { return false; }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }
}
