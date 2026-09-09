using MSUIClient.Net;

namespace MSUIClient;

// Networking / account settings for connecting to the VMaNGOS server (Phase 2).
// Lives in a partial file so the only change to ClientConfig.cs itself is adding
// `partial` to the class declaration. Disabled by default: the client stays a
// pure offline world-viewer until you set server.enabled = true and fill in an
// account. RealmdHost / RealmdPort are the existing top-level ClientConfig fields.
public sealed partial class ClientConfig
{
    public ServerConfig Server { get; set; } = new();

    public sealed class ServerConfig
    {
        /// <summary>Master opt-in. False keeps the client fully offline (behaviour unchanged).</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Connect automatically on launch when Enabled.</summary>
        public bool AutoConnect { get; set; } = true;

        /// <summary>
        /// Enable the versioned REAL_PORTALS extension used by the matching
        /// SuperUI core. This custom-core client defaults it on so an existing
        /// per-machine config automatically gains the feature after an update.
        /// Set false when connecting to a stock server: stock opcode tables do
        /// not know the SuperUI capability probe or portal prepare packets.
        /// </summary>
        public bool RealPortals { get; set; } = true;

        /// <summary>Account name (case-insensitive; sent uppercased, like the retail login box).</summary>
        public string Account { get; set; } = "";

        /// <summary>Account password. Kept in client-config.json, which is gitignored.</summary>
        public string Password { get; set; } = "";

        /// <summary>Preferred available realm. Null/empty shows a chooser when multiple realms are advertised.</summary>
        public string? Realm { get; set; }

        /// <summary>Character to log in as. Null/empty leaves the worker at character selection.</summary>
        public string? Character { get; set; }

        /// <summary>
        /// World-server port, used ONLY if the realm-list entry carries no explicit port (it usually
        /// does). Your vmangos deploy may map mangosd to 8085 or 18085 — the realm list is authoritative.
        /// </summary>
        public int WorldPortFallback { get; set; } = 8085;

        /// <summary>
        /// Connect the world server on the same host as realmd, ignoring the realm-list's advertised
        /// address (private servers usually run mangosd + realmd on one box, and the realmlist DB often
        /// advertises an internal/unreachable IP). The advertised PORT is still used. Set false only if
        /// your world server is genuinely on a different, reachable host.
        /// </summary>
        public bool WorldUsesRealmdHost { get; set; } = true;

        /// <summary>Connect / handshake timeout in milliseconds.</summary>
        public int TimeoutMs { get; set; } = 10000;
    }

    /// <summary>Build the Net-layer settings from this config.</summary>
    public NetSettings ToNetSettings() => new(
        RealmdHost, RealmdPort, Server.WorldPortFallback,
        Server.Account, Server.Password,
        string.IsNullOrWhiteSpace(Server.Realm) ? null : Server.Realm,
        string.IsNullOrWhiteSpace(Server.Character) ? null : Server.Character,
        Server.TimeoutMs, Server.WorldUsesRealmdHost);
}
