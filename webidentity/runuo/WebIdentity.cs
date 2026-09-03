// ClassicUO Web Identity 0xA4 — handler implementation.
//
// Adapted from the upstream ClassicUO/packets reference impl for
// Memento (RunUO 2.x). One mandatory adaptation:
//
//   - The upstream reflection on `NetState.<Address>k__BackingField`
//     assumes Address is a compiler-generated auto-property (ServUO
//     and ModernUO both declare it that way). Memento's NetState
//     declares Address manually with backing field `m_Address`
//     (Source/System/Network/NetState.cs:47). We reflect on
//     `m_Address` here instead. If a future engine refactor restores
//     the auto-property shape, swap to `<Address>k__BackingField`.
//
// Wire format: see docs/sphere-source-x-adapted/WEBIDENTITY.md or
// https://github.com/ClassicUO/packets/blob/main/WebIdentity.ksy

using Server.Network;
using System;
using System.Net;
using System.Reflection;
using static Server.ClassicUO.ClassicUONetwork;

namespace Server.ClassicUO
{
    /// <summary>
    /// Event arguments used when a web identity event occurs.
    /// </summary>
    public class ClassicUOWebIdentityEventArgs
    {
        public DateTimeOffset Timestamp { get; protected set; }
        public string Secret { get; protected set; }
        public string UserId { get; protected set; }
        public string ConnectingIp { get; protected set; }
        public string ExternalAuthProvider { get; protected set; }
        public string ExternalAuthUsername { get; protected set; }
        public string ExternalAuthId { get; protected set; }
        public string Role { get; protected set; }

        public static ClassicUOWebIdentityEventArgs DeserializeFromPacket(PacketReader reader) =>
            new ClassicUOWebIdentityEventArgs()
            {
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(reader.ReadUInt32()),
                Secret = reader.ReadUTF8(),
                UserId = reader.ReadUTF8(),
                ConnectingIp = reader.ReadUTF8(),
                ExternalAuthProvider = reader.ReadUTF8(),
                ExternalAuthUsername = reader.ReadUTF8(),
                ExternalAuthId = reader.ReadUTF8(),
                Role = reader.ReadUTF8()
            };

        public override string ToString() =>
            string.Format(
                "{{ Timestamp = {0}, Secret = ***, UserId = {1}, ConnectingIp = {2}, ExternalAuthProvider = {3}, ExternalAuthUsername = {4}, ExternalAuthId = {5}, Role = {6} }}",
                Timestamp,
                UserId,
                ConnectingIp,
                ExternalAuthProvider,
                ExternalAuthUsername,
                ExternalAuthId,
                Role
            );
    }

    /// <summary>
    /// Handles the WebClientIdentity packet that's sent via the 0xA4
    /// SystemInfo packet. We intercept 0xA4 because:
    ///
    /// 1. It's an unused packet in modern UO — only sent by OSI clients
    ///    for telemetry, and the reference RunUO 2.x SystemInfo handler
    ///    just reads + drops the bytes.
    /// 2. All emulators accept it before any Account/GameServer login,
    ///    making it a good IPLimiter workaround.
    /// 3. It's large enough (149 bytes) for our use, and the engine
    ///    already has a slot for it.
    ///
    /// Settings live in World/Info/Scripts/Settings.cs (MySettings):
    ///   S_WebIdentitySecret               (must match proxy YAML)
    ///   S_WebIdentityKickOnBadSecret      (true: drop bad-secret packets)
    ///   S_WebIdentityIpLimitWorkaround    (true: rewrite NetState.Address)
    /// </summary>
    public static partial class WebIdentity
    {
        public static readonly string WebIdentitySecret =
            GetOrUpdateConfig("ClassicUO.WebIdentitySecret", "CHANGEME");

        public static readonly bool WebIdentityKickOnBadSecret =
            GetOrUpdateConfig("ClassicUO.WebIdentityKickOnBadSecret", true);

        public static readonly bool WebIdentityIpLimitWorkaround =
            GetOrUpdateConfig("ClassicUO.WebIdentityIpLimitWorkaround", true);

        /// <summary>
        /// Fires after a valid WebIdentity 0xA4 packet is consumed. Scripts
        /// can subscribe to read UserId / Role / ExternalAuth* and act on
        /// them (e.g. auto-grant in-game admin handles to authenticated
        /// Discord shard-owners).
        /// </summary>
        public static event Action<ClassicUOWebIdentityEventArgs> OnWebIdentityReceived;

        public static void Configure()
        {
            Console.WriteLine("[WebIdentity] Configure() registering 0xA4 handler (secret-len={0}, kick={1}, rewriteIP={2})",
                WebIdentitySecret?.Length ?? 0, WebIdentityKickOnBadSecret, WebIdentityIpLimitWorkaround);
            RegisterHandler(0xA4, 149, false, WebIdentityInterceptSystemInfo);
        }

        private static bool WebIdentityInterceptSystemInfo(NetState ns, PacketReader reader, int packetLength)
        {
            Console.WriteLine("[WebIdentity] 0xA4 received from {0} (len={1})", ns, packetLength);
            var clientType = reader.ReadUTF8Safe(6);
            if (clientType != "CUOWEB")
            {
                Console.WriteLine("[WebIdentity] clientType='{0}' != CUOWEB → falling through to SystemInfo", clientType);
                return false;
            }
            Console.WriteLine("[WebIdentity] CUOWEB tag matched");

            var version = reader.ReadByte();
            if (version > 1)
            {
                Log("Ident received newer packet version than expected (v{0}) is there an update? {1}", version, ns);
            }

            var args = ClassicUOWebIdentityEventArgs.DeserializeFromPacket(reader);
            var validSecret = WebIdentitySecret == args.Secret;
            Console.WriteLine("[WebIdentity] parsed: ConnectingIp={0} UserId={1} Role={2} validSecret={3} (got-len={4}, expected-len={5})",
                args.ConnectingIp, args.UserId, args.Role, validSecret, args.Secret?.Length ?? 0, WebIdentitySecret?.Length ?? 0);

            if (!validSecret && WebIdentityKickOnBadSecret)
            {
                Console.WriteLine("[WebIdentity] INVALID SECRET — disconnecting {0}", ns);
                ns.Disconnect(string.Format("Incorrect secret from IP {0}, disconnecting", args.ConnectingIp));
                return true;
            }

            var identAge = (DateTimeOffset.Now - args.Timestamp).TotalSeconds;
            if (identAge > 30)
            {
                ns.Disconnect(string.Format("Timestamp expired, {0} seconds old from IP {1}, disconnecting", identAge, args.ConnectingIp));
                return true;
            }

            // Only override NetState IP if the secret is valid — otherwise
            // anyone could forge this packet to spoof their IP.
            if (validSecret && WebIdentityIpLimitWorkaround)
            {
                if (IPAddress.TryParse(args.ConnectingIp, out var userConnectingIp))
                {
                    ReflectionOverrideNetstateAddress(ns, userConnectingIp);
                }
                else
                {
                    ns.Disconnect(string.Format("Ident contained a malformed UserConnectingIp '{0}', disconnecting", args.ConnectingIp));
                    return true;
                }
            }

            OnWebIdentityReceived?.Invoke(args);
            Log("Ident processed {0}", args);

            return true;
        }

        /// <summary>
        /// Overrides NetState.Address (private field m_Address in RunUO 2.x —
        /// upstream auto-property `&lt;Address&gt;k__BackingField` doesn't apply here)
        /// and the cached m_ToString string so audit/log lines show the
        /// real client IP, not the docker-bridge IP.
        /// </summary>
        private static void ReflectionOverrideNetstateAddress(NetState state, IPAddress addr)
        {
            var addrField = typeof(NetState).GetField("m_Address", BindingFlags.Instance | BindingFlags.NonPublic);
            var toStrField = typeof(NetState).GetField(NSToStringFieldPropertyName, BindingFlags.Instance | BindingFlags.NonPublic);
            Console.WriteLine("[WebIdentity] reflection: m_Address-found={0}, m_ToString-found={1}, target={2}",
                addrField != null, toStrField != null, addr);
            addrField?.SetValue(state, addr);
            toStrField?.SetValue(state, addr.ToString());
            Console.WriteLine("[WebIdentity] after override: NetState.Address={0}, NetState.ToString={1}", state.Address, state);
        }
    }
}
