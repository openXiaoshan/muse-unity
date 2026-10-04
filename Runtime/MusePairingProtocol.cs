// Muse Gadget pairing v5 / wong2/muse-client port; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;

namespace Muse.Unity
{
    public sealed class MusePairingIdentity
    {
        public readonly string Mac, NodeId, DeviceId, Name;
        public MusePairingIdentity(string mac)
        {
            if (mac == null || !Regex.IsMatch(mac, "^[0-9a-f]{2}(:[0-9a-f]{2}){5}$")) throw new MuseException("PAIR_IDENTITY_INVALID");
            Mac = mac; string suffix = mac.Replace(":", "").Substring(6);
            NodeId = "homelink-" + suffix; DeviceId = "hatch-link:" + mac; Name = "MuseGadget" + suffix.ToUpperInvariant();
        }
        public static MusePairingIdentity Create()
        {
            var bytes = MuseCrypto.Random(6); bytes[0] = (byte)((bytes[0] & 0xfc) | 2);
            return new MusePairingIdentity(string.Join(":", bytes.Select(v => v.ToString("x2"))));
        }
    }

    public sealed class MusePairingSession : IDisposable
    {
        private const string Label = "hatch-link ble setup v1";
        private readonly Func<double> now;
        private readonly byte[] fixedPrivate, fixedNonce;
        private byte[] rx, tx;
        private string sessionId;
        private ulong received, sent;
        private double deadline;
        private int phase;
        public readonly MusePairingIdentity Identity;
        public readonly string Version;
        public MusePairingSession(MusePairingIdentity identity, string version = "0.1.0", byte[] testPrivate = null, byte[] testNonce = null, Func<double> testClock = null)
        {
            Identity = identity; Version = version; fixedPrivate = testPrivate; fixedNonce = testNonce;
            now = testClock ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        }
        public static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        public static byte[] Decode(string value, int limit = 16384)
        {
            if (string.IsNullOrEmpty(value) || value.Length > limit || value.Length % 4 == 1 || !Regex.IsMatch(value, "^[A-Za-z0-9_-]+$"))
                throw new MuseException("PAIR_ENCODING_INVALID");
            return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
        }
        public JObject Info() => new JObject { ["device_id"] = Identity.DeviceId, ["mac"] = Identity.Mac, ["model"] = "hatch_link",
            ["pairing_protocol"] = 5, ["pairing_auth"] = "none", ["pairing_auth_epoch"] = 0, ["pairing_policy"] = "confirm_app" };
        public JObject Hello(JObject command)
        {
            Dispose();
            try
            {
                if ((int?)command["version"] != 5 || (string)command["pairing_auth"] != "none" || (string)command["pairing_policy"] != "confirm_app")
                    throw new MuseException("PAIR_HELLO_INVALID");
                var mobilePub = Decode((string)command["mobile_pub"]); var mobileNonce = Decode((string)command["mobile_nonce"]);
                if (mobilePub.Length != 65 || mobilePub[0] != 4 || mobileNonce.Length != 16) throw new MuseException("PAIR_KEY_INVALID");
                var curve = SecNamedCurves.GetByName("secp256r1"); var domain = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
                byte[] scalarBytes; BigInteger scalar;
                do { scalarBytes = fixedPrivate == null ? MuseCrypto.Random(32) : (byte[])fixedPrivate.Clone(); scalar = new BigInteger(1, scalarBytes); }
                while (fixedPrivate == null && (scalar.SignValue == 0 || scalar.CompareTo(curve.N) >= 0));
                Array.Clear(scalarBytes, 0, scalarBytes.Length);
                if (scalar.SignValue == 0 || scalar.CompareTo(curve.N) >= 0) throw new MuseException("PAIR_KEY_INVALID");
                var pub = curve.G.Multiply(scalar).Normalize().GetEncoded(false);
                var nonce = fixedNonce == null ? MuseCrypto.Random(16) : (byte[])fixedNonce.Clone();
                if (nonce.Length != 16) throw new MuseException("PAIR_KEY_INVALID");
                string transcript = string.Join("\n", new[] { "hatch-link-pairing-v5", "version=5", "initiator_role=mobile", "responder_role=link",
                    "device_id=" + Identity.DeviceId, "node_id=" + Identity.NodeId, "mac=" + Identity.Mac, "model=hatch_link", "firmware_version=" + Version,
                    "selected_cipher_suite=p256-hkdf-sha256-aes-gcm-v1", "pairing_auth=none", "pairing_auth_epoch=0", "pairing_policy=confirm_app", "confirm_timeout_seconds=0",
                    "mobile_pub=" + Encode(mobilePub), "device_pub=" + Encode(pub), "mobile_nonce=" + Encode(mobileNonce), "device_nonce=" + Encode(nonce) });
                var hash = MuseCrypto.Hash(Encoding.UTF8.GetBytes(transcript));
                var agreement = new ECDHBasicAgreement(); agreement.Init(new ECPrivateKeyParameters(scalar, domain));
                var raw = agreement.CalculateAgreement(new ECPublicKeyParameters(curve.Curve.DecodePoint(mobilePub), domain)).ToByteArrayUnsigned();
                var shared = new byte[32]; Buffer.BlockCopy(raw, 0, shared, 32 - raw.Length, raw.Length); Array.Clear(raw, 0, raw.Length);
                var extracted = MuseCrypto.Hmac(MuseCrypto.Hash(mobileNonce, nonce, hash), shared);
                var secret = MuseCrypto.Hmac(extracted, MuseCrypto.Join(Encoding.UTF8.GetBytes(Label), new byte[] { 1 }));
                rx = MuseCrypto.Hmac(secret, MuseCrypto.Join(Encoding.UTF8.GetBytes("mobile->device"), new byte[] { 1 }));
                tx = MuseCrypto.Hmac(secret, MuseCrypto.Join(Encoding.UTF8.GetBytes("device->mobile"), new byte[] { 1 }));
                sessionId = Encode(MuseCrypto.Slice(MuseCrypto.Hash(Encoding.UTF8.GetBytes("hatch-link session id v1"), hash, shared), 0, 16));
                foreach (var bytes in new[] { shared, extracted, secret }) Array.Clear(bytes, 0, bytes.Length);
                received = sent = 0; phase = 1; deadline = now() + 60;
                var result = Info(); result["type"] = "pairing_ready"; result["version"] = 5; result["node_id"] = Identity.NodeId;
                result["firmware_version"] = Version; result["device_pub"] = Encode(pub); result["device_nonce"] = Encode(nonce);
                result["transcript_hash"] = Encode(hash); result["session_id"] = sessionId; return result;
            }
            catch { Dispose(); throw new MuseException("PAIR_HELLO_INVALID"); }
        }
        private void Live()
        { if (phase == 0 || now() > deadline) { Dispose(); throw new MuseException("PAIR_SESSION_EXPIRED"); } }
        private static byte[] Iv(byte direction, ulong counter)
        { var value = new byte[12]; value[0] = direction; for (int i = 11; i >= 4; i--) { value[i] = (byte)counter; counter >>= 8; } return value; }
        public JObject Decrypt(JObject envelope)
        {
            try
            {
                Live(); string count = (string)envelope["counter"];
                if (count == null || !Regex.IsMatch(count, "^[0-9]{1,20}$") || !ulong.TryParse(count, out var number) || number != received || received == ulong.MaxValue || (string)envelope["session_id"] != sessionId)
                    throw new MuseException("PAIR_COUNTER_INVALID");
                var tag = Decode((string)envelope["tag"]); if (tag.Length != 16) throw new MuseException("PAIR_TAG_INVALID");
                var plain = MuseCrypto.Gcm(false, rx, Iv(0, received), MuseCrypto.Join(Decode((string)envelope["ciphertext"]), tag),
                    Encoding.UTF8.GetBytes(Label + "|" + sessionId + "|m2d|" + received));
                try { var result = JObject.Parse(new UTF8Encoding(false, true).GetString(plain)); received++; return result; }
                finally { Array.Clear(plain, 0, plain.Length); }
            }
            catch { Dispose(); throw new MuseException("PAIR_DECRYPT_FAILED"); }
        }
        public void Confirm(JObject command)
        {
            Live();
            if (phase != 1 || received != 1 || command.Count != 1 || (string)command["action"] != "pairing_client_finished")
            { Dispose(); throw new MuseException("PAIR_CONFIRM_INVALID"); }
            phase = 2; deadline = now() + 120;
        }
        public void AssertConfirmed() { Live(); if (phase < 2) throw new MuseException("PAIR_CONFIRM_REQUIRED"); }
        public void Provision() { AssertConfirmed(); phase = 3; deadline = now() + 120; }
        public JObject Encrypt(JObject value)
        {
            Live(); if (sent == ulong.MaxValue) { Dispose(); throw new MuseException("PAIR_COUNTER_EXHAUSTED"); }
            ulong counter = sent++; var sealedValue = MuseCrypto.Gcm(true, tx, Iv(1, counter), Encoding.UTF8.GetBytes(value.ToString(Formatting.None)),
                Encoding.UTF8.GetBytes(Label + "|" + sessionId + "|d2m|" + counter));
            return new JObject { ["type"] = "pairing_encrypted", ["session_id"] = sessionId, ["counter"] = counter.ToString(),
                ["ciphertext"] = Encode(MuseCrypto.Slice(sealedValue, 0, sealedValue.Length - 16)), ["tag"] = Encode(MuseCrypto.Slice(sealedValue, sealedValue.Length - 16, 16)) };
        }
        public void Dispose()
        { foreach (var value in new[] { rx, tx }) if (value != null) Array.Clear(value, 0, value.Length); rx = tx = null; phase = 0; deadline = 0; sessionId = null; }
    }

    public sealed class MuseBlePackets
    {
        private readonly MemoryStream bytes = new MemoryStream();
        private int total, next;
        public void Reset() { bytes.SetLength(0); total = next = 0; }
        public byte[] Feed(byte[] packet)
        {
            if (packet == null || packet.Length == 0 || packet.Length > 8192) { Reset(); throw new MuseException("PAIR_PACKET_INVALID"); }
            if (packet.Length < 3 || packet[0] != 0xfe) return packet;
            int index = packet[1], count = packet[2];
            if (index == 0) { Reset(); total = count; }
            if (count == 0 || total != count || index != next || index >= count) { Reset(); throw new MuseException("PAIR_PACKET_ORDER_INVALID"); }
            bytes.Write(packet, 3, packet.Length - 3); next++;
            if (bytes.Length > 8192) { Reset(); throw new MuseException("PAIR_MESSAGE_TOO_LARGE"); }
            if (next != total) return null;
            var result = bytes.ToArray(); Reset(); return result;
        }
        public static IEnumerable<byte[]> Encode(JObject value)
        {
            var raw = Encoding.UTF8.GetBytes(value.ToString(Formatting.None)); int count = Math.Max(1, (raw.Length + 16) / 17);
            if (count > 255) throw new MuseException("PAIR_MESSAGE_TOO_LARGE");
            for (int i = 0; i < count; i++) yield return MuseCrypto.Join(new[] { (byte)0xfe, (byte)i, (byte)count }, MuseCrypto.Slice(raw, i * 17, Math.Min(17, raw.Length - i * 17)));
        }
    }
}
