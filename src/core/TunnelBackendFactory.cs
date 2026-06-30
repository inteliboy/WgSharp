using System;

namespace WgSharp.Core
{
    /// <summary>
    /// Decides which ITunnelBackend a given config should run on, in exactly
    /// one place — both MainForm (in-process activation) and WgSharpService
    /// (the background-service path) call this instead of each re-deciding
    /// AppSettings.UseWireGuardNt for themselves, so the AmneziaWG rule below
    /// can't accidentally drift out of sync between the two.
    ///
    /// The rule: an AmneziaWG config (see Config.IsAmneziaWg) ALWAYS runs on
    /// the managed Tunnel backend, regardless of the user's WireGuardNT
    /// setting. WireGuardNT is WireGuard LLC's own closed-source, digitally
    /// signed kernel driver — we don't own that code and can't change its
    /// wire format, so it will never be able to speak AWG's disguised framing
    /// (custom junk packets, header bytes, S-padding; see AwgFraming.cs). The
    /// managed Tunnel is the implementation WgSharp wrote from scratch, where
    /// every byte of the handshake/transport framing is genuinely ours to
    /// change — that's the only place AWG support could go.
    /// </summary>
    public static class TunnelBackendFactory
    {
        /// <summary>
        /// True if this config can only run on the managed backend — i.e.
        /// WireGuardNT must be skipped for it even if the user has that
        /// setting on. Currently just AmneziaWG, but kept as its own check
        /// (rather than inlining Config.IsAmneziaWg at every call site) in
        /// case a future managed-only feature needs the same treatment.
        /// </summary>
        public static bool RequiresManagedBackend(Config cfg)
        {
            return cfg != null && cfg.IsAmneziaWg;
        }

        /// <summary>
        /// Creates the right backend for this config. preferWireGuardNt is
        /// normally AppSettings.UseWireGuardNt, passed in explicitly (rather
        /// than read here) so callers can log/branch on whether the
        /// preference was actually honored.
        /// </summary>
        public static ITunnelBackend Create(Config cfg, bool preferWireGuardNt)
        {
            if (RequiresManagedBackend(cfg) || !preferWireGuardNt)
                return new Tunnel(cfg);
            return new WireGuardNtTunnel(cfg);
        }
    }
}
