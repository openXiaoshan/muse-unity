// Adapted from wong2/muse-client and Meta Muse Gadget SDK; see NOTICE.txt.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Muse.Unity
{
    public sealed class MuseException : Exception
    {
        public readonly string Code;
        public readonly int Status;
        public MuseException(string code, int status = 0) : base(code) { Code = code; Status = status; }
    }

    public static class MuseCrypto
    {
        public static byte[] Random(int count)
        { var result = new byte[count]; using var rng = RandomNumberGenerator.Create(); rng.GetBytes(result); return result; }
        public static byte[] Join(params byte[][] values)
        { using var stream = new MemoryStream(); foreach (var value in values) stream.Write(value, 0, value.Length); return stream.ToArray(); }
        public static byte[] Slice(byte[] value, int offset, int length)
        { var result = new byte[length]; Buffer.BlockCopy(value, offset, result, 0, length); return result; }
        public static byte[] Hash(params byte[][] values)
        { using var hash = SHA256.Create(); return hash.ComputeHash(Join(values)); }
        public static byte[] Hmac(byte[] key, byte[] data)
        { using var hmac = new HMACSHA256(key); return hmac.ComputeHash(data); }
        public static (byte[] first, byte[] second) Hkdf(byte[] key, byte[] input)
        {
            var temp = Hmac(key, input); var first = Hmac(temp, new byte[] { 1 });
            var second = Hmac(temp, Join(first, new byte[] { 2 })); Array.Clear(temp, 0, temp.Length);
            return (first, second);
        }
        public static byte[] PublicKey(byte[] secret) => new X25519PrivateKeyParameters(secret, 0).GeneratePublicKey().GetEncoded();
        public static byte[] Dh(byte[] secret, byte[] peer)
        {
            if (secret.Length != 32 || peer.Length != 32) throw new MuseException("INVALID_X25519_KEY");
            var result = new byte[32];
            try { new X25519PrivateKeyParameters(secret, 0).GenerateSecret(new X25519PublicKeyParameters(peer, 0), result, 0); }
            catch { throw new MuseException("INVALID_X25519_PEER"); }
            return result;
        }
        public static byte[] Gcm(bool encrypt, byte[] key, byte[] iv, byte[] data, byte[] ad)
        {
            try
            {
                var cipher = new GcmBlockCipher(new AesEngine());
                cipher.Init(encrypt, new AeadParameters(new KeyParameter(key), 128, iv, ad));
                var output = new byte[cipher.GetOutputSize(data.Length)];
                int length = cipher.ProcessBytes(data, 0, data.Length, output, 0);
                length += cipher.DoFinal(output, length);
                return length == output.Length ? output : Slice(output, 0, length);
            }
            catch { throw new MuseException("NOISE_AUTHENTICATION_FAILED"); }
        }
    }

    public sealed class MuseCipher : IDisposable
    {
        private readonly byte[] key;
        private ulong nonce;
        private bool dead;
        public MuseCipher(byte[] key = null) { this.key = key == null ? null : (byte[])key.Clone(); }
        private byte[] Crypt(bool encrypt, byte[] data, byte[] ad)
        {
            if (dead || nonce >= 9007199254740991UL) throw new MuseException("NOISE_CIPHER_UNAVAILABLE");
            if (key == null) return (byte[])data.Clone();
            // Muse's AES-GCM nonce is 4 zero bytes + BIG-endian uint64, including during XX.
            var iv = new byte[12]; ulong current = nonce++;
            for (int i = 11; i >= 4; i--) { iv[i] = (byte)current; current >>= 8; }
            try { return MuseCrypto.Gcm(encrypt, key, iv, data, ad ?? Array.Empty<byte>()); }
            catch { Dispose(); throw; }
        }
        public byte[] Encrypt(byte[] data, byte[] ad = null) => Crypt(true, data, ad);
        public byte[] Decrypt(byte[] data, byte[] ad = null) => Crypt(false, data, ad);
        public void Dispose() { dead = true; if (key != null) Array.Clear(key, 0, key.Length); }
    }

    public sealed class MuseNoiseHandshake : IDisposable
    {
        private byte[] h = new byte[32], ck, ephemeral, remote;
        private readonly byte[] fixedEphemeral, fixedStatic;
        private MuseCipher cipher = new MuseCipher();
        private int phase;
        public MuseNoiseHandshake(byte[] fixedEphemeral = null, byte[] fixedStatic = null)
        {
            this.fixedEphemeral = fixedEphemeral; this.fixedStatic = fixedStatic;
            var name = Encoding.ASCII.GetBytes("Noise_XX_25519_AESGCM_SHA256");
            Buffer.BlockCopy(name, 0, h, 0, name.Length); ck = (byte[])h.Clone(); MixHash(Array.Empty<byte>());
        }
        private void MixHash(byte[] data) { h = MuseCrypto.Hash(h, data); }
        private void MixKey(byte[] data)
        {
            var keys = MuseCrypto.Hkdf(ck, data); Array.Clear(ck, 0, ck.Length); ck = keys.first;
            cipher.Dispose(); cipher = new MuseCipher(keys.second); Array.Clear(keys.second, 0, keys.second.Length);
            Array.Clear(data, 0, data.Length);
        }
        private byte[] Encrypt(byte[] data) { var result = cipher.Encrypt(data, h); MixHash(result); return result; }
        private byte[] Decrypt(byte[] data) { var result = cipher.Decrypt(data, h); MixHash(data); return result; }
        public byte[] Message1()
        {
            if (phase != 0) throw new MuseException("INVALID_HANDSHAKE_STATE");
            ephemeral = fixedEphemeral == null ? MuseCrypto.Random(32) : (byte[])fixedEphemeral.Clone();
            var pub = MuseCrypto.PublicKey(ephemeral); MixHash(pub); Encrypt(Array.Empty<byte>()); phase = 1; return pub;
        }
        public void ReceiveMessage2(byte[] message)
        {
            if (phase != 1) throw new MuseException("INVALID_HANDSHAKE_STATE"); phase = -1;
            if (message.Length < 96) throw new MuseException("NOISE_MESSAGE_TOO_SHORT");
            remote = MuseCrypto.Slice(message, 0, 32); MixHash(remote); MixKey(MuseCrypto.Dh(ephemeral, remote));
            var remoteStatic = Decrypt(MuseCrypto.Slice(message, 32, 48));
            MixKey(MuseCrypto.Dh(ephemeral, remoteStatic)); Decrypt(MuseCrypto.Slice(message, 80, message.Length - 80)); phase = 2;
        }
        public (byte[] message, MuseCipher send, MuseCipher receive) Finish()
        {
            if (phase != 2) throw new MuseException("INVALID_HANDSHAKE_STATE"); phase = -1;
            var secret = fixedStatic == null ? MuseCrypto.Random(32) : (byte[])fixedStatic.Clone();
            try
            {
                var encrypted = Encrypt(MuseCrypto.PublicKey(secret)); MixKey(MuseCrypto.Dh(secret, remote));
                var message = MuseCrypto.Join(encrypted, Encrypt(Array.Empty<byte>()));
                var keys = MuseCrypto.Hkdf(ck, Array.Empty<byte>());
                var send = new MuseCipher(keys.first); var receive = new MuseCipher(keys.second);
                Array.Clear(keys.first, 0, keys.first.Length); Array.Clear(keys.second, 0, keys.second.Length);
                Dispose(); return (message, send, receive);
            }
            finally { Array.Clear(secret, 0, secret.Length); }
        }
        public void Dispose()
        {
            phase = -1; cipher.Dispose();
            foreach (var value in new[] { h, ck, ephemeral, remote }) if (value != null) Array.Clear(value, 0, value.Length);
        }
    }
}
