using System;

namespace WgSharp.Core
{
    /// <summary>
    /// Common surface for a tunnel engine, so the app can swap between the
    /// from-scratch managed implementation (Tunnel) and the kernel WireGuardNT
    /// implementation (WireGuardNtTunnel) behind a setting. The UI only ever uses
    /// these members, so either backend is a drop-in.
    /// </summary>
    public interface ITunnelBackend
    {
        event Action<string> LogMessage;
        void Start();
        void Stop();
        TunnelStatus GetStatus();
    }
}
