using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Muse.Unity
{
    // Called on Unity's main thread. Awaited verification is guarded against cancellation/new hellos.
    public sealed class MusePairingController : IDisposable
    {
        private readonly MusePairingSession session;
        private readonly MuseBlePackets packets = new MuseBlePackets();
        private readonly string sdkToken;
        private readonly Func<bool> online;
        private readonly Action<byte[]> send;
        private readonly Func<MuseCredentials, CancellationToken, Task> verify;
        private readonly Action<MuseCredentials> commit, completed;
        private readonly Action<string> progress, failed;
        private readonly bool discoveryOnly;
        private readonly Action<string> trace;
        private CancellationTokenSource pending = new CancellationTokenSource();
        private int generation;
        private bool stopped, provisioning;
        public MusePairingController(MusePairingSession session, string sdkToken, Func<bool> online, Action<byte[]> send,
            Func<MuseCredentials, CancellationToken, Task> verify, Action<MuseCredentials> commit, Action<MuseCredentials> completed, Action<string> progress, Action<string> failed, bool discoveryOnly = false, Action<string> trace = null)
        { this.session = session; this.sdkToken = sdkToken; this.online = online; this.send = send; this.verify = verify; this.commit = commit; this.completed = completed; this.progress = progress; this.failed = failed; this.discoveryOnly = discoveryOnly; this.trace = trace; }
        private void Send(JObject value) { foreach (var packet in MuseBlePackets.Encode(value)) send(packet); }
        private void Status(string status)
        {
            var value = new JObject { ["type"] = "status", ["status"] = status };
            if (status == "pairing_confirmed") value["sdk_token"] = sdkToken;
            Send(session.Encrypt(value));
        }
        public async Task ReceiveAsync(byte[] packet)
        {
            if (stopped) return;
            try
            {
                var raw = packets.Feed(packet); if (raw == null) return;
                var command = JObject.Parse(new UTF8Encoding(false, true).GetString(raw));
                bool encrypted = (string)command["action"] == "pairing_encrypted";
                if (encrypted) command = session.Decrypt(command);
                string action = (string)command["action"];
                // Whitelisted phase names only: never expose command values, tokens or ciphertext.
                trace?.Invoke(action == "get_device_info" || action == "pairing_client_hello" || action == "pairing_client_finished" || action == "wifi_scan" || action == "provision_v2" ? action : "unknown_action");
                if (action == "get_device_info")
                {
                    var info = session.Info(); info["type"] = "device_info"; info["node_id"] = session.Identity.NodeId;
                    info["version"] = session.Version; info["build_sha"] = ""; info["network_ready"] = online(); Send(info); return;
                }
                if (discoveryOnly) { Dispose(); failed("PAIR_SDK_TOKEN_REQUIRED"); return; }
                if (!encrypted && action == "pairing_client_hello")
                {
                    generation++; pending.Cancel(); pending.Dispose(); pending = new CancellationTokenSource(); provisioning = false;
                    Send(session.Hello(command)); progress("Phone connected. Confirm this device in Muse."); return;
                }
                // Credentials and provisioning commands are never accepted in plaintext.
                if (!encrypted) throw new MuseException("PAIR_ENCRYPTION_REQUIRED");
                if (action == "pairing_client_finished")
                { session.Confirm(command); Status("pairing_confirmed"); progress("Muse app confirmed. Choose Use current connection."); return; }
                session.AssertConfirmed();
                if (action == "wifi_scan")
                {
                    Send(session.Encrypt(new JObject { ["type"] = "wifi_scan_result", ["networks"] = online()
                        ? new JArray(new JObject { ["ssid"] = "Use current connection", ["rssi"] = -40, ["secure"] = false }) : new JArray() }));
                    trace?.Invoke("wifi_option_sent"); progress("Wi-Fi option sent. Waiting for phone to send device credentials…"); return;
                }
                if (action != "provision_v2") { Status("error_unknown_action"); return; }
                if (provisioning) { Status("error_operation_in_progress"); return; }
                if ((string)command["token_type"] != "device" || command["ssid"]?.Type != JTokenType.String || command["password"]?.Type != JTokenType.String ||
                    string.IsNullOrWhiteSpace((string)command["access_token"]) || string.IsNullOrWhiteSpace((string)command["refresh_token"]))
                { Status("error_missing_credentials"); return; }
                session.Provision(); provisioning = true; int current = generation;
                var credentialValue = (JObject)command.DeepClone(); credentialValue["device_id"] = session.Identity.NodeId;
                var credentials = MuseCredentials.Parse(credentialValue.ToString(), null, sdkToken);
                credentials.SavedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Status("wifi_connecting");
                if (!online()) { Status("wifi_failed"); provisioning = false; progress("Connect this device to the internet, then retry pairing."); return; }
                Status("wifi_connected"); progress("Checking Muse authorization…"); trace?.Invoke("verifying_credentials");
                try { await verify(credentials, pending.Token); }
                catch (Exception error) { if (stopped || current != generation) return;
                    trace?.Invoke(error is MuseException e && e.Status > 0 ? "authorization_http_" + e.Status : "authorization_request_failed");
                    Status("auth_failed"); throw new MuseException("PAIR_AUTH_FAILED"); }
                if (stopped || current != generation) return;
                session.AssertConfirmed();
                try { commit(credentials); }
                catch { Status("error_storage"); throw new MuseException("PAIR_STORAGE_FAILED"); }
                trace?.Invoke("credentials_saved"); Status("auth_ok"); completed(credentials); Dispose();
            }
            catch (Exception error)
            { if (!stopped) { Dispose(); failed(error is MuseException muse ? muse.Code : "PAIR_PROTOCOL_FAILED"); } }
            finally { if (packet != null) Array.Clear(packet, 0, packet.Length); }
        }
        public void Dispose()
        { if (stopped) return; stopped = true; generation++; pending.Cancel(); pending.Dispose(); session.Dispose(); packets.Reset(); }
    }
}
