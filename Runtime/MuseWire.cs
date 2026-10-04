// Adapted from wong2/muse-client and Meta Muse Gadget SDK; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Muse.Unity
{
    public static class MuseWire
    {
        public const int MaxChunk = 65489;
        public static byte[] Varint(ulong value)
        { var bytes = new List<byte>(); while (value >= 128) { bytes.Add((byte)((value & 127) | 128)); value >>= 7; } bytes.Add((byte)value); return bytes.ToArray(); }
        public static byte[] Integer(int field, ulong value) => MuseCrypto.Join(Varint((ulong)field * 8), Varint(value));
        public static byte[] Bytes(int field, byte[] value) => MuseCrypto.Join(Varint((ulong)field * 8 + 2), Varint((ulong)value.Length), value);
        public static byte[] Text(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));
        private static ulong Read(byte[] data, ref int offset)
        {
            ulong value = 0;
            for (int i = 0; i < 10; i++)
            {
                if (offset >= data.Length) throw new MuseException("INVALID_PROTOBUF_VARINT");
                byte b = data[offset++]; if (i == 9 && b > 1) throw new MuseException("INVALID_PROTOBUF_VARINT");
                value |= (ulong)(b & 127) << (7 * i); if ((b & 128) == 0) return value;
            }
            throw new MuseException("INVALID_PROTOBUF_VARINT");
        }
        public static Dictionary<int, List<object>> Fields(byte[] data)
        {
            var result = new Dictionary<int, List<object>>(); int offset = 0;
            while (offset < data.Length)
            {
                ulong key = Read(data, ref offset); ulong fieldValue = key >> 3; int wire = (int)(key & 7);
                if (fieldValue < 1 || fieldValue > 0x1fffffff || fieldValue >= 19000 && fieldValue <= 19999)
                    throw new MuseException("INVALID_PROTOBUF_FIELD");
                int field = (int)fieldValue; object value;
                if (wire == 0) value = Read(data, ref offset);
                else if (wire == 2 || wire == 1 || wire == 5)
                {
                    ulong size = wire == 2 ? Read(data, ref offset) : (ulong)(wire == 1 ? 8 : 4);
                    if (size > (ulong)(data.Length - offset)) throw new MuseException("TRUNCATED_PROTOBUF_FIELD");
                    value = MuseCrypto.Slice(data, offset, (int)size); offset += (int)size; if (wire != 2) continue;
                }
                else throw new MuseException("UNSUPPORTED_PROTOBUF_WIRE");
                if (!result.TryGetValue(field, out var list)) result[field] = list = new List<object>();
                list.Add(value);
            }
            return result;
        }
        public static byte[] GetBytes(Dictionary<int, List<object>> fields, int field)
        {
            if (!fields.TryGetValue(field, out var values)) return Array.Empty<byte>();
            return values[values.Count - 1] as byte[] ?? throw new MuseException("EXPECTED_PROTOBUF_BYTES");
        }
        public static ulong GetInt(Dictionary<int, List<object>> fields, int field, ulong fallback = 0)
        {
            if (!fields.TryGetValue(field, out var values)) return fallback;
            return values[values.Count - 1] is ulong value ? value : throw new MuseException("EXPECTED_PROTOBUF_INTEGER");
        }
        public static byte[] Request(int id, string path, byte[] body)
        {
            var parts = new List<byte[]> { Text(1, "POST"), Text(2, path) };
            foreach (var header in new Dictionary<string, string> {
                { "Content-Type", "application/json" }, { "accept", "application/x-ndjson" },
                { "x-request-id", Guid.NewGuid().ToString() }, { "x-app-id", "hatch-web" } })
                parts.Add(Bytes(3, MuseCrypto.Join(Text(1, header.Key), Text(2, header.Value))));
            parts.Add(Bytes(4, body)); parts.Add(Integer(5, 1));
            return Bytes(2, MuseCrypto.Join(Integer(1, (ulong)id), Bytes(2, MuseCrypto.Join(parts.ToArray()))));
        }
        public static byte[] Reset(int id) => Bytes(2, MuseCrypto.Join(Integer(1, (ulong)id), Bytes(5, Integer(1, 1))));
        public sealed class Response
        { public int StreamId, Status; public string Kind; public byte[] Body; public bool End; }
        public static Response DecodeResponse(byte[] data)
        {
            var frame = Fields(GetBytes(Fields(data), 1)); ulong id = GetInt(frame, 1);
            if (id == 0 || id > int.MaxValue) throw new MuseException("INVALID_STREAM_ID");
            if (new[] { 2, 3, 4, 5 }.Count(frame.ContainsKey) != 1) throw new MuseException("AMBIGUOUS_SERVICE_FRAME");
            if (frame.ContainsKey(3))
            {
                var value = Fields(GetBytes(frame, 3)); ulong status = GetInt(value, 1);
                if (status < 100 || status > 599) throw new MuseException("INVALID_RESPONSE_STATUS");
                return new Response { StreamId = (int)id, Status = (int)status, Kind = "response", Body = GetBytes(value, 3), End = GetInt(value, 4) != 0 };
            }
            if (frame.ContainsKey(4))
            { var value = Fields(GetBytes(frame, 4)); return new Response { StreamId = (int)id, Kind = "chunk", Body = GetBytes(value, 1), End = GetInt(value, 2) != 0 }; }
            if (frame.ContainsKey(5)) return new Response { StreamId = (int)id, Kind = "reset", Body = Array.Empty<byte>(), End = true };
            throw new MuseException("UNEXPECTED_SERVER_REQUEST");
        }
        public static IEnumerable<byte[]> Chunks(byte[] data, ulong? messageId = null)
        {
            var random = MuseCrypto.Random(8); ulong id = messageId ?? BitConverter.ToUInt64(random, 0);
            int total = Math.Max(1, (data.Length + MaxChunk - 1) / MaxChunk);
            if (total > 256) throw new MuseException("NOISE_MESSAGE_TOO_LARGE");
            for (int i = 0; i < total; i++) yield return MuseCrypto.Join(Integer(1, id), Integer(2, (ulong)i),
                Integer(3, (ulong)total), Bytes(4, MuseCrypto.Slice(data, i * MaxChunk, Math.Min(MaxChunk, data.Length - i * MaxChunk))));
        }
    }

    public sealed class MuseFrameAssembler
    {
        private sealed class Assembly
        { public int Total; public long Created; public readonly Dictionary<int, byte[]> Parts = new Dictionary<int, byte[]>(); }
        private readonly Dictionary<ulong, Assembly> pending = new Dictionary<ulong, Assembly>();
        private bool dead;
        public byte[] Decode(byte[] data)
        {
            if (dead) throw new MuseException("NOISE_DECODER_UNAVAILABLE");
            try
            {
                var fields = MuseWire.Fields(data); ulong id = MuseWire.GetInt(fields, 1), index = MuseWire.GetInt(fields, 2), total = MuseWire.GetInt(fields, 3, 1);
                var payload = MuseWire.GetBytes(fields, 4);
                if (total < 1 || total > 256 || index >= total || payload.Length > MuseWire.MaxChunk) throw new MuseException("INVALID_NOISE_CHUNK");
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var key in pending.Where(p => now - p.Value.Created > 60000).Select(p => p.Key).ToArray()) pending.Remove(key);
                if (!pending.TryGetValue(id, out var assembly))
                {
                    if (pending.Count >= 16) throw new MuseException("TOO_MANY_NOISE_ASSEMBLIES");
                    pending[id] = assembly = new Assembly { Total = (int)total, Created = now };
                }
                if (assembly.Total != (int)total || assembly.Parts.ContainsKey((int)index)) throw new MuseException("INCONSISTENT_NOISE_CHUNKS");
                assembly.Parts[(int)index] = payload; if (assembly.Parts.Count != assembly.Total) return null;
                pending.Remove(id); return MuseCrypto.Join(Enumerable.Range(0, assembly.Total).Select(i => assembly.Parts[i]).ToArray());
            }
            catch { dead = true; pending.Clear(); throw; }
        }
    }
}
