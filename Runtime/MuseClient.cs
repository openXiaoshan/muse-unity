// Adapted from wong2/muse-client; see NOTICE.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Muse.Unity
{
    // Never serialize this into a scene/Prefab/Resources or PlayerPrefs.
    public sealed class MuseCredentials
    {
        public string AccessToken, RefreshToken, DeviceId, SdkToken, ApiUrl, NoiseHost;
        // Local configuration only. Never sent in Muse API requests or BLE provisioning.
        public string AsrKey;
        public long SavedAt;
        public static MuseCredentials Parse(string json, string identityJson = null, string sdkToken = null)
        {
            var value = JObject.Parse(json);
            var result = new MuseCredentials {
                AccessToken = (string)value["access_token"] ?? (string)value["accessToken"],
                RefreshToken = (string)value["refresh_token"] ?? (string)value["refreshToken"],
                DeviceId = (string)value["device_id"] ?? (string)value["deviceId"] ?? (string)value["node_id"],
                SdkToken = sdkToken ?? (string)value["sdk_token"] ?? (string)value["sdkToken"],
                ApiUrl = (string)value["api_url_v2"] ?? (string)value["apiUrl"],
                NoiseHost = (string)value["noise_host"] ?? (string)value["noiseHost"],
                AsrKey = (string)value["elevenlabs_asr_key"],
                SavedAt = (long?)value["access_token_saved_at"] ?? (long?)value["savedAt"] ?? 0 };
            if (string.IsNullOrEmpty(result.DeviceId) && !string.IsNullOrEmpty(identityJson))
            {
                string mac = (string)JObject.Parse(identityJson)["mac"];
                if (mac != null && System.Text.RegularExpressions.Regex.IsMatch(mac, "^[0-9a-f]{2}(:[0-9a-f]{2}){5}$"))
                    result.DeviceId = "homelink-" + mac.Replace(":", "").Substring(6);
            }
            if (string.IsNullOrWhiteSpace(result.AccessToken) || string.IsNullOrWhiteSpace(result.DeviceId))
                throw new MuseException("MUSE_PAIRED_CREDENTIALS_REQUIRED");
            return result;
        }
    }

    public sealed class MuseAccount : IDisposable
    {
        private readonly HttpClient http;
        private readonly SemaphoreSlim refreshLock = new SemaphoreSlim(1, 1);
        private readonly Action<MuseCredentials> onCredentials;
        public MuseCredentials Credentials { get; private set; }
        public MuseAccount(MuseCredentials credentials, Action<MuseCredentials> onCredentials = null, HttpMessageHandler handler = null)
        {
            Credentials = credentials ?? throw new MuseException("MUSE_PAIRED_CREDENTIALS_REQUIRED");
            this.onCredentials = onCredentials;
            http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
            http.Timeout = TimeSpan.FromSeconds(15);
        }
        private async Task<JObject> CallAsync(string path, string token, JObject body, CancellationToken cancellation)
        {
            var root = new Uri(string.IsNullOrEmpty(Credentials.ApiUrl) ? "https://api.muse.ai" : Credentials.ApiUrl);
            if (root.Scheme != "https" || !string.IsNullOrEmpty(root.UserInfo)) throw new MuseException("MUSE_REQUIRES_TLS");
            using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, new Uri(root, path));
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            request.Headers.TryAddWithoutValidation("X-API-Version", "1.0.0");
            request.Headers.TryAddWithoutValidation("User-Agent", "muse-unity/0.1.0-preview.1");
            if (body != null) request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new MuseException("MUSE_HTTP_FAILED", (int)response.StatusCode);
            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (json.Length > 1024 * 1024) throw new MuseException("MUSE_RESPONSE_TOO_LARGE");
            return JObject.Parse(json);
        }
        public async Task RefreshAsync(CancellationToken cancellation)
        {
            await refreshLock.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                var current = Credentials;
                if (string.IsNullOrEmpty(current.RefreshToken)) throw new MuseException("MUSE_REFRESH_TOKEN_REQUIRED");
                var body = new JObject { ["device_id"] = current.DeviceId };
                if (!string.IsNullOrEmpty(current.SdkToken)) body["sdk_token"] = current.SdkToken;
                string raw = current.RefreshToken.Split(':').Last();
                var response = await CallAsync("/device_token/refresh", "hatch_refresh:" + raw, body, cancellation).ConfigureAwait(false);
                var value = response["payload"] as JObject ?? response;
                string access = (string)value["access_token"], refresh = (string)value["refresh_token"];
                if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh)) throw new MuseException("MUSE_INVALID_REFRESH");
                Credentials = new MuseCredentials { AccessToken = access, RefreshToken = refresh, DeviceId = current.DeviceId,
                    SdkToken = current.SdkToken, ApiUrl = current.ApiUrl, NoiseHost = current.NoiseHost, AsrKey = current.AsrKey, SavedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                onCredentials?.Invoke(Credentials);
            }
            finally { refreshLock.Release(); }
        }
        public async Task<JArray> ListVmsAsync(CancellationToken cancellation)
        {
            if (Credentials.SavedAt > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Credentials.SavedAt >= 10800 && !string.IsNullOrEmpty(Credentials.RefreshToken))
                await RefreshAsync(cancellation).ConfigureAwait(false);
            JObject result;
            try { result = await CallAsync("/fetch_vms", Credentials.AccessToken, null, cancellation).ConfigureAwait(false); }
            catch (MuseException e) when (e.Status == 401 && !string.IsNullOrEmpty(Credentials.RefreshToken))
            { await RefreshAsync(cancellation).ConfigureAwait(false); result = await CallAsync("/fetch_vms", Credentials.AccessToken, null, cancellation).ConfigureAwait(false); }
            return result["vm_list"] as JArray ?? throw new MuseException("MUSE_VM_LIST_MISSING");
        }
        public void Dispose() { http.Dispose(); }
    }

    public sealed class MuseChatEventDecoder
    {
        private readonly Decoder decoder = new UTF8Encoding(false, true).GetDecoder();
        private readonly StringBuilder buffer = new StringBuilder();
        private long sequence;
        public IEnumerable<JObject> Feed(byte[] data, bool final = false)
        {
            var chars = new char[Encoding.UTF8.GetMaxCharCount(data.Length)];
            int count = decoder.GetChars(data, 0, data.Length, chars, 0, final); buffer.Append(chars, 0, count);
            if (buffer.Length > 1024 * 1024) throw new MuseException("MUSE_EVENT_TOO_LARGE");
            string text = buffer.ToString(); int offset = 0, newline;
            while ((newline = text.IndexOf('\n', offset)) >= 0)
            { var value = Parse(text.Substring(offset, newline - offset)); offset = newline + 1; if (value != null) yield return value; }
            buffer.Clear(); buffer.Append(text.Substring(offset));
            if (final) { var value = Parse(buffer.ToString()); buffer.Clear(); if (value != null) yield return value; }
        }
        private JObject Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var value = JObject.Parse(text); if ((string)value["type"] != "event") return null;
            long seq = (long?)value["seq"] ?? 0; if (seq > 0 && seq <= sequence) return null; if (seq > 0) sequence = seq;
            if (value["event"]?.Type != JTokenType.String && value["event_name"]?.Type == JTokenType.String) value["event"] = value["event_name"];
            if (value["event"]?.Type != JTokenType.String || !(value["payload"] is JObject)) throw new MuseException("MUSE_INVALID_EVENT");
            return value;
        }
    }

    public sealed class MuseChatAcknowledgement
    {
        public readonly string MessageId, ParentMessageId;
        public MuseChatAcknowledgement(string messageId, string parentMessageId = null)
        { MessageId = messageId; ParentMessageId = parentMessageId; }
        public static MuseChatAcknowledgement Parse(JObject raw)
        {
            var result = raw["result"] as JObject ?? raw;
            string id = (string)result["message_id"];
            if (string.IsNullOrEmpty(id)) throw new MuseException("MUSE_ACK_ID_MISSING");
            return new MuseChatAcknowledgement(id, (string)result["reply_to_message_id"]);
        }
    }
    public interface IMuseChatClient : IDisposable
    {
        bool IsConnected { get; }
        string VmName { get; }
        Task ConnectAsync(CancellationToken cancellation);
        Task<MuseChatAcknowledgement> SendMessageAsync(string text, string sessionId, CancellationToken cancellation);
        Task SubscribeAsync(string sessionId, Action<JObject> onEvent, CancellationToken cancellation, Action onReady = null);
    }

    public sealed class MuseClient : IMuseChatClient
    {
        private readonly MuseAccount account;
        private MuseNoiseConnection connection;
        public string VmName { get; private set; }
        public bool IsConnected => connection != null && connection.IsConnected;
        public MuseClient(MuseCredentials credentials, Action<MuseCredentials> onCredentials = null)
        { account = new MuseAccount(credentials, onCredentials); }
        public async Task ConnectAsync(CancellationToken cancellation)
        {
            connection?.Dispose(); connection = null;
            var vms = await account.ListVmsAsync(cancellation).ConfigureAwait(false);
            var vm = vms.OfType<JObject>().FirstOrDefault(v => (bool?)v["default"] == true) ?? vms.OfType<JObject>().FirstOrDefault();
            if (vm == null) throw new MuseException("MUSE_VM_UNAVAILABLE");
            string id = (string)vm["vm_id"], token = (string)vm["vm_auth_token"];
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(token)) throw new MuseException("MUSE_VM_INVALID");
            string host = string.IsNullOrEmpty(account.Credentials.NoiseHost) ? "hatch.metaaivm.com" : account.Credentials.NoiseHost;
            if (host.IndexOfAny(new[] { '/', '@', '?', '#', '\\', ' ', '\n', '\r', '\t' }) >= 0) throw new MuseException("MUSE_INVALID_NOISE_HOST");
            connection = await MuseNoiseConnection.ConnectAsync(new Uri("wss://" + host + "/v1/noise?vm_id=" + Uri.EscapeDataString(id)), token, cancellation).ConfigureAwait(false);
            VmName = (string)vm["vm_name"] ?? "Muse";
        }
        private static byte[] Json(JObject value) => Encoding.UTF8.GetBytes(value.ToString(Formatting.None));
        private static void Check(MuseResponse response)
        { if (response.Status < 200 || response.Status >= 300) throw new MuseException("MUSE_CHAT_REJECTED", response.Status); }
        public async Task<MuseChatAcknowledgement> SendMessageAsync(string text, string sessionId, CancellationToken cancellation)
        {
            if (!IsConnected) throw new MuseException("MUSE_NOT_CONNECTED");
            if (string.IsNullOrWhiteSpace(text) || text.Length > 12000) throw new MuseException("MUSE_EMPTY_OR_LONG_TEXT");
            var body = new JObject { ["message"] = text, ["output_modality"] = "text" };
            if (!string.IsNullOrEmpty(sessionId)) body["session_id"] = sessionId;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(60000);
            using var response = await connection.OpenAsync("/chat/stream", Json(body), timeout.Token).ConfigureAwait(false); Check(response);
            using var bytes = new MemoryStream(); byte[] chunk;
            while ((chunk = await response.Body.ReadAsync(timeout.Token).ConfigureAwait(false)) != null)
            { if (bytes.Length + chunk.Length > 1024 * 1024) throw new MuseException("MUSE_ACK_TOO_LARGE"); bytes.Write(chunk, 0, chunk.Length); }
            return MuseChatAcknowledgement.Parse(JObject.Parse(Encoding.UTF8.GetString(bytes.ToArray())));
        }
        public async Task SubscribeAsync(string sessionId, Action<JObject> onEvent, CancellationToken cancellation, Action onReady = null)
        {
            if (!IsConnected) throw new MuseException("MUSE_NOT_CONNECTED");
            var body = new JObject(); if (!string.IsNullOrEmpty(sessionId)) body["session_id"] = sessionId;
            using var response = await connection.OpenAsync("/chat/subscribe", Json(body), cancellation).ConfigureAwait(false); Check(response);
            onReady?.Invoke();
            var decoder = new MuseChatEventDecoder(); byte[] chunk;
            while ((chunk = await response.Body.ReadAsync(cancellation).ConfigureAwait(false)) != null)
                foreach (var value in decoder.Feed(chunk)) onEvent?.Invoke(value);
            foreach (var value in decoder.Feed(Array.Empty<byte>(), true)) onEvent?.Invoke(value);
            cancellation.ThrowIfCancellationRequested(); throw new MuseException("MUSE_SUBSCRIPTION_ENDED");
        }
        public void Dispose() { connection?.Dispose(); connection = null; account.Dispose(); }
    }
}
