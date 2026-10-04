using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Muse.Unity;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class VerifyMuseObject
{
    private static readonly List<string> report = new List<string>();
    static VerifyMuseObject()
    {
        EditorApplication.playModeStateChanged += state => {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool("MuseVerifyPending", false))
            { SessionState.SetBool("MuseVerifyPending", false); RuntimeChecks(); }
        };
    }
    private static void Check(bool pass, string label)
    { report.Add((pass ? "PASS " : "FAIL ") + label); File.WriteAllLines("report.txt", report); if (!pass) throw new Exception(label); }
    private static byte[] Hex(string value) => Enumerable.Range(0, value.Length / 2).Select(i => Convert.ToByte(value.Substring(i * 2, 2), 16)).ToArray();
    private static void Equal(byte[] a, byte[] b, string label) => Check(a.SequenceEqual(b), label);
    private static void Throws(Action action, string label)
    { bool threw = false; try { action(); } catch { threw = true; } Check(threw, label); }
    private static async Task Reject(Task task, string label)
    { bool threw = false; try { await task; } catch { threw = true; } Check(threw, label); }
    public static async void Run()
    {
        try
        {
            await PairingChecks();
            Directory.CreateDirectory("Temp/MuseGradleFixture");
            File.WriteAllText("Temp/MuseGradleFixture/proguard-unity.txt", "# existing Unity rules\n");
            MuseAndroidBuild.AddKeepRules("Temp/MuseGradleFixture"); MuseAndroidBuild.AddKeepRules("Temp/MuseGradleFixture");
            string rules = File.ReadAllText("Temp/MuseGradleFixture/proguard-unity.txt");
            Check(rules.StartsWith("# existing Unity rules") && rules.Split(new[] { "# Muse Unity JNI" }, StringSplitOptions.None).Length == 2 && rules.Contains("-keep class io.github.openxiaoshan.muse.**"),
                "Android Gradle callback preserves Muse JNI names without duplicating or replacing Unity rules");
            var vector = JObject.Parse(File.ReadAllText("noise-vector.json")); Func<string, byte[]> bytes = key => Hex((string)vector[key]);
            using var noise = new MuseNoiseHandshake(bytes("ephemeral"), bytes("static"));
            Equal(noise.Message1(), bytes("message1"), "Unity X25519 message 1 matches independent Python vector");
            noise.ReceiveMessage2(bytes("message2")); var keys = noise.Finish();
            Equal(keys.message, bytes("message3"), "Unity Noise XX message 3 matches independent Python vector");
            Equal(keys.send.Encrypt(bytes("requestPlain")), bytes("requestCipher"), "Unity AES-GCM outbound key and BIG-endian nonce match reference");
            Equal(keys.receive.Decrypt(bytes("responseCipher")), bytes("responsePlain"), "Unity inbound ciphertext matches independent Python vector");
            Throws(() => noise.Finish(), "handshake state rejects reuse");
            Throws(() => keys.receive.Decrypt(bytes("responseCipher")), "replayed ciphertext is rejected");
            Throws(() => keys.receive.Decrypt(bytes("responseCipher")), "failed cipher remains poisoned");
            var malformed = bytes("message2"); malformed[50] ^= 1;
            using var broken = new MuseNoiseHandshake(bytes("ephemeral"), bytes("static")); broken.Message1();
            Throws(() => broken.ReceiveMessage2(malformed), "tampered XX handshake rejected");
            var response = MuseWire.DecodeResponse(bytes("responsePlain"));
            Check(response.StreamId == 7 && response.Status == 200 && response.End && Encoding.UTF8.GetString(response.Body) == "{\"ok\":true}", "protobuf response matches Python envelope");
            var payload = Enumerable.Repeat((byte)42, 180000).ToArray(); var chunks = MuseWire.Chunks(payload, ulong.MaxValue).ToArray();
            var assembler = new MuseFrameAssembler(); Check(assembler.Decode(chunks[2]) == null && assembler.Decode(chunks[0]) == null, "out-of-order Noise chunks wait for full assembly");
            Equal(assembler.Decode(chunks[1]), payload, "uint64 frame IDs and 180 KB messages reassemble");
            var duplicate = new MuseFrameAssembler(); duplicate.Decode(chunks[0]); Throws(() => duplicate.Decode(chunks[0]), "duplicate fragments rejected");
            Throws(() => duplicate.Decode(chunks[1]), "failed assembler stays poisoned");
            Throws(() => MuseWire.Fields(new byte[] { 10, 255 }), "malformed protobuf varint rejected");
            Throws(() => MuseWire.Fields(new byte[] { 10, 3, 1 }), "truncated protobuf field rejected");
            Throws(() => MuseWire.Fields(new byte[] { 0 }), "field zero rejected");
            await Reject(MuseNoiseConnection.ConnectAsync(new Uri("ws://example.test"), "fake", CancellationToken.None), "production transport rejects plaintext WebSocket");

            var decoder = new MuseChatEventDecoder();
            var item = new JObject { ["type"] = "event", ["event"] = "delta.text_append", ["seq"] = 1, ["payload"] = new JObject { ["message_id"] = "reply", ["text"] = "你好🌍" } };
            var wire = Encoding.UTF8.GetBytes("{\"type\":\"ack\"}\n" + item.ToString(Newtonsoft.Json.Formatting.None) + "\n");
            int split = Array.IndexOf(wire, (byte)0xe5) + 1;
            Check(!decoder.Feed(wire.Take(split).ToArray()).Any(), "split UTF-8 and acknowledgement buffered without emitting a partial event");
            var decoded = decoder.Feed(wire.Skip(split).ToArray()).ToArray();
            Check(decoded.Length == 1 && (string)decoded[0]["payload"]["text"] == "你好🌍", "Chinese/emoji NDJSON survives arbitrary network split");
            Check(!decoder.Feed(Encoding.UTF8.GetBytes(item.ToString(Newtonsoft.Json.Formatting.None) + "\n")).Any(), "replayed event seq is deduplicated");
            Throws(() => new MuseChatEventDecoder().Feed(new byte[] { 0xe4, 0xbd }, true).ToArray(), "truncated UTF-8 rejected");
            var ack = MuseChatAcknowledgement.Parse(JObject.Parse("{\"result\":{\"message_id\":\"user\",\"reply_to_message_id\":\"canonical-user\"}}"));
            Check(ack.MessageId == "user" && ack.ParentMessageId == "canonical-user", "send acknowledgement preserves canonical parent used by SDK reply matching");
            var canonical = new MuseReplyCollector("ours", ack.MessageId, ack.ParentMessageId);
            Check(canonical.Receive(JObject.Parse("{\"event_name\":\"message.assistant\",\"payload\":{\"message_id\":\"reply\",\"parent_message_id\":\"canonical-user\",\"text\":\"canonical reply\"}}")) && canonical.Text == "canonical reply",
                "SDK event-name, parent ID and final text aliases retain a related reply");
            Check(!canonical.Receive(JObject.Parse("{\"event\":\"message.assistant\",\"payload\":{\"message_id\":\"bad\",\"parent_message_id\":\"unrelated\",\"text\":\"foreign\"}}")) && canonical.LastDecision == "other_parent",
                "alternate parent support still rejects unrelated replies with safe decision codes");
            var mainReply = new MuseReplyCollector(null, "user", "canonical-user", scopedSubscription: false);
            Check(!mainReply.Receive(JObject.Parse("{\"event\":\"message.assistant\",\"payload\":{\"message_id\":\"foreign\",\"text\":\"background App activity\"}}")),
                "main conversation subscription does not display uncorrelated App activity");
            mainReply.Receive(JObject.Parse("{\"event\":\"delta.message_start\",\"payload\":{\"message_id\":\"ours-reply\",\"reply_to_message_id\":\"canonical-user\"}}"));
            Check(mainReply.Receive(JObject.Parse("{\"event\":\"delta.text_append\",\"payload\":{\"message_id\":\"ours-reply\",\"text\":\"related stream\"}}")) && mainReply.Text == "related stream",
                "related message-start links subsequent deltas without parent fields");
            var collector = new MuseReplyCollector("ours", "user");
            Func<string, string, string, JObject> chat = (kind, id, text) => new JObject { ["event"] = kind, ["payload"] = new JObject { ["message_id"] = id, ["text"] = text } };
            var foreign = chat("delta.text_append", "bad", "foreign"); foreign["payload"]["session_id"] = "other";
            Check(!collector.Receive(foreign), "other session's text never displayed");
            var wrongReply = chat("delta.text_append", "bad", "foreign"); wrongReply["payload"]["reply_to_message_id"] = "other-user";
            Check(!collector.Receive(wrongReply), "explicit mismatched reply-to excluded");
            collector.Receive(chat("delta.text_append", "reply", "Hello")); collector.Receive(chat("delta.text_append", "reply", " world"));
            Check(collector.Text == "Hello world" && !collector.MessageReady, "live partial reply does not claim message completion");
            var final = chat("delta.message_done", "reply", ""); final["payload"]["display_text"] = "Hello world!"; collector.Receive(final);
            Check(collector.Text == "Hello world!" && collector.MessageReady, "final text replaces partial reply without duplication");

            var wav = MuseAsr.EncodeWav(new float[] { 1, -1, .5f, .5f, float.NaN, 0 }, 2, 16000);
            Check(Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && BitConverter.ToInt32(wav, 40) == 6 && BitConverter.ToInt16(wav, 44) == 0 && BitConverter.ToInt16(wav, 46) == 16384, "independent recorder encodes valid mono PCM WAV and downmixes stereo");
            var asrHandler = new Handler(async request => {
                Check(request.RequestUri.AbsoluteUri == "https://api.elevenlabs.io/v1/speech-to-text", "ASR goes directly to existing ElevenLabs provider");
                string data = await request.Content.ReadAsStringAsync();
                Check(data.Contains("scribe_v1") && data.Contains("muse-voice.wav") && !data.Contains("language_code"), "ASR multipart sends WAV with automatic language detection");
                return JsonResponse("{\"text\":\"  打开番茄钟  \"}");
            });
            Check(await MuseAsr.TranscribeAsync(wav, "fixture-only", CancellationToken.None, asrHandler) == "打开番茄钟", "only trimmed ASR transcript is returned for Muse sending");
            await Reject(MuseAsr.TranscribeAsync(wav, "fixture-only", CancellationToken.None, new Handler(_ => Task.FromResult(JsonResponse("{\"text\":\" \"}")))), "empty ASR transcript rejected before Muse send");
            var credential = MuseCredentials.Parse("{\"access_token\":\"old\",\"refresh_token\":\"prefix:refresh\"}", "{\"mac\":\"02:01:02:ab:cd:ef\"}");
            Check(credential.DeviceId == "homelink-abcdef", "original pairing.json plus identity.json imports expected device ID");
            credential.AsrKey = "fixture-local-asr";
            Check(MuseCredentials.Parse(MuseDeviceStore.Json(credential)).AsrKey == credential.AsrKey,
                "secure configuration serialization restores ASR key alongside Muse binding");
            Check(MuseAsrConfiguration.Resolve(" explicit ", "saved") == "explicit" && MuseAsrConfiguration.Resolve("", "saved") == "saved",
                "explicit ASR override takes priority and blank input reuses saved configuration");
            int calls = 0; MuseCredentials rotated = null;
            using var account = new MuseAccount(credential, next => rotated = next, new Handler(request => {
                calls++;
                if (request.RequestUri.AbsolutePath == "/device_token/refresh") {
                    Check(request.Headers.Authorization.ToString() == "Bearer hatch_refresh:refresh", "refresh strips upstream token prefix");
                    Check(!request.Content.ReadAsStringAsync().Result.Contains("fixture-local-asr"), "local ASR key is excluded from Muse refresh request");
                    return Task.FromResult(JsonResponse("{\"payload\":{\"access_token\":\"new\",\"refresh_token\":\"next\"}}"));
                }
                if (request.Headers.Authorization.ToString() == "Bearer old") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                return Task.FromResult(JsonResponse("{\"vm_list\":[]}"));
            }));
            await account.ListVmsAsync(CancellationToken.None);
            Check(calls == 3 && rotated.AccessToken == "new" && rotated.RefreshToken == "next", "401 refresh retries VM lookup once and returns rotated credentials");
            Check(rotated.AsrKey == "fixture-local-asr", "Muse token refresh preserves local ASR configuration");

            if (File.Exists("fixture-port.txt")) await Interop(int.Parse(File.ReadAllText("fixture-port.txt")));
            else Check(false, "independent Python protocol server must be supplied");
            // Only this isolated project: save its empty startup scene so Unity permits additive creation.
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), "Assets/FixtureStart.unity");
            MuseObjectTools.CreateAssets();
            Check(File.Exists(MuseObjectTools.PrefabPath) && File.Exists(MuseObjectTools.ScenePath), "optional standalone Prefab and test scene created by Unity API");
            Check(!EditorBuildSettings.scenes.Any(s => s.path == MuseObjectTools.ScenePath), "test scene is not installed in build startup");
            File.WriteAllLines("edit-report.txt", report); SessionState.SetBool("MuseVerifyPending", true);
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(MuseObjectTools.ScenePath); EditorApplication.isPlaying = true;
        }
        catch (Exception error) { File.WriteAllLines("report.txt", report.Concat(new[] { "ERROR " + error })); EditorApplication.Exit(1); }
    }
    private const string PairToken = "mgst_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static JObject PairVector => JObject.Parse(File.ReadAllText("pairing-vector.json"));
    private static MusePairingSession PairSession(Func<double> clock = null)
    {
        var v = PairVector;
        return new MusePairingSession(new MusePairingIdentity((string)v["mac"]), (string)v["firmware_version"],
            Hex((string)v["device_private_scalar_hex"]), MusePairingSession.Decode((string)v["device_nonce"]), clock);
    }
    private static JObject PairHello() => new JObject { ["action"] = "pairing_client_hello", ["version"] = 5, ["pairing_auth"] = "none", ["pairing_policy"] = "confirm_app",
        ["mobile_pub"] = PairVector["mobile_pub"], ["mobile_nonce"] = PairVector["mobile_nonce"] };
    private static JObject PairFinished() => new JObject { ["action"] = "pairing_encrypted", ["session_id"] = PairVector["session_id"], ["counter"] = "0",
        ["ciphertext"] = PairVector["client_finished_ciphertext"], ["tag"] = PairVector["client_finished_tag"] };
    private static JObject PairProvision() => new JObject { ["action"] = "provision_v2", ["ssid"] = "Use current connection", ["password"] = "", ["token_type"] = "device",
        ["access_token"] = "fixture-only-access", ["refresh_token"] = "fixture-only-refresh", ["api_url_v2"] = "https://example.test", ["noise_host"] = "example.test" };
    private static JObject PhoneSeal(JObject command, ulong counter)
    {
        byte[] nonce = new byte[12]; ulong n = counter; for (int i = 11; i >= 4; i--) { nonce[i] = (byte)n; n >>= 8; }
        var raw = MuseCrypto.Gcm(true, Hex((string)PairVector["mobile_tx_key_hex"]), nonce, Encoding.UTF8.GetBytes(command.ToString(Newtonsoft.Json.Formatting.None)),
            Encoding.UTF8.GetBytes("hatch-link ble setup v1|" + PairVector["session_id"] + "|m2d|" + counter));
        return new JObject { ["action"] = "pairing_encrypted", ["session_id"] = PairVector["session_id"], ["counter"] = counter.ToString(),
            ["ciphertext"] = MusePairingSession.Encode(raw.Take(raw.Length - 16).ToArray()), ["tag"] = MusePairingSession.Encode(raw.Skip(raw.Length - 16).ToArray()) };
    }
    private static JObject PhoneOpen(JObject envelope)
    {
        ulong count = ulong.Parse((string)envelope["counter"]); byte[] nonce = new byte[12]; nonce[0] = 1; ulong n = count;
        for (int i = 11; i >= 4; i--) { nonce[i] = (byte)n; n >>= 8; }
        var raw = MuseCrypto.Gcm(false, Hex((string)PairVector["mobile_rx_key_hex"]), nonce,
            MuseCrypto.Join(MusePairingSession.Decode((string)envelope["ciphertext"]), MusePairingSession.Decode((string)envelope["tag"])),
            Encoding.UTF8.GetBytes("hatch-link ble setup v1|" + PairVector["session_id"] + "|d2m|" + count));
        return JObject.Parse(Encoding.UTF8.GetString(raw));
    }
    private static async Task PairSend(MusePairingController controller, JObject command)
    { foreach (var packet in MuseBlePackets.Encode(command)) await controller.ReceiveAsync(packet); }
    private static async Task PairingChecks()
    {
        using var session = PairSession(); var ready = session.Hello(PairHello());
        foreach (string key in new[] { "device_pub", "device_nonce", "transcript_hash", "session_id" })
            Check((string)ready[key] == (string)PairVector[key], "BLE v5 P-256/HKDF matches official community_app vector: " + key);
        session.Confirm(session.Decrypt(PairFinished()));
        var confirmed = session.Encrypt(new JObject { ["type"] = "status", ["status"] = "pairing_confirmed", ["sdk_token"] = PairToken });
        Equal(MuseCrypto.Join(MusePairingSession.Decode((string)confirmed["ciphertext"]), MusePairingSession.Decode((string)confirmed["tag"])),
            Hex((string)PairVector["confirmed_cipher_hex"]), "BLE outbound AES-GCM nonce/AAD matches independent Python mobile vector");
        Throws(() => session.Decrypt(PairFinished()), "BLE replay poisons pairing keys");
        Throws(() => session.Encrypt(new JObject()), "BLE failed session cannot continue");
        using var tamper = PairSession(); tamper.Hello(PairHello()); var bad = PairFinished(); bad["tag"] = "AAAAAAAAAAAAAAAAAAAAAA";
        Throws(() => tamper.Decrypt(bad), "BLE tampered authorization rejected");
        using var wrongConfirm = PairSession(); wrongConfirm.Hello(PairHello());
        Throws(() => wrongConfirm.Confirm(wrongConfirm.Decrypt(PhoneSeal(new JObject { ["action"] = "wifi_scan" }, 0))), "BLE only first encrypted client-finished confirms consent");
        double now = 0; using var expired = PairSession(() => now); expired.Hello(PairHello()); now = 61;
        Throws(() => expired.Decrypt(PairFinished()), "BLE authorization handshake expires");
        var decoder = new MuseBlePackets(); var fragments = MuseBlePackets.Encode(PairHello()).ToArray();
        Check(fragments.All(p => p.Length <= 20), "BLE notifications fit default 20-byte payload"); decoder.Feed(fragments[0]);
        Throws(() => decoder.Feed(fragments[2]), "BLE missing/out-of-order fragment rejected");
        var large = Encoding.UTF8.GetBytes(new JObject { ["action"] = "provision_v2", ["fixture_padding"] = new string('x', 2400) }.ToString(Newtonsoft.Json.Formatting.None));
        byte[] largeResult = null; var largePackets = new MuseBlePackets(); int largeCount = (large.Length + 508) / 509;
        for (int i = 0; i < largeCount; i++)
            largeResult = largePackets.Feed(MuseCrypto.Join(new[] { (byte)0xfe, (byte)i, (byte)largeCount }, large.Skip(i * 509).Take(509).ToArray()));
        Equal(largeResult, large, "BLE receives Android-sized 512-byte fragments for a credential-length message");
        bool verified = false, saved = false, complete = false; var messages = new List<JObject>(); var incoming = new MuseBlePackets(); var errors = new List<string>();
        using var controller = new MusePairingController(PairSession(), PairToken, () => true,
            packet => { byte[] raw = incoming.Feed(packet); if (raw != null) messages.Add(JObject.Parse(Encoding.UTF8.GetString(raw))); },
            (c, ct) => { Check(c.DeviceId == "homelink-000001" && c.SdkToken == PairToken, "BLE credentials retain stable device identity and SDK token"); verified = true; return Task.CompletedTask; },
            c => { Check(verified, "BLE device authorization verified before encrypted storage commit"); saved = true; }, c => complete = true, _ => { }, code => errors.Add(code));
        await PairSend(controller, new JObject { ["action"] = "get_device_info" }); await PairSend(controller, PairHello()); await PairSend(controller, PairFinished());
        await PairSend(controller, PhoneSeal(new JObject { ["action"] = "wifi_scan" }, 1)); await PairSend(controller, PhoneSeal(PairProvision(), 2));
        var statuses = messages.Where(v => (string)v["type"] == "pairing_encrypted").Select(PhoneOpen).ToArray();
        Check(saved && complete && errors.Count == 0 && statuses.Where(v => (string)v["type"] == "status").Select(v => (string)v["status"]).SequenceEqual(
            new[] { "pairing_confirmed", "wifi_connecting", "wifi_connected", "auth_ok" }), "BLE phone authorization/provisioning flow returns encrypted auth_ok");
        Check(statuses.Any(v => (string)v["type"] == "wifi_scan_result" && (string)v["networks"][0]["ssid"] == "Use current connection"), "BLE uses current internet connection without changing Wi-Fi");
        foreach (bool replaceHello in new[] { false, true })
        {
            var wait = new TaskCompletionSource<bool>(); bool committed = false; CancellationToken verification = default;
            using var pending = new MusePairingController(PairSession(), PairToken, () => true, _ => { },
                (_, ct) => { verification = ct; return wait.Task; }, _ => committed = true, _ => committed = true, _ => { }, _ => { });
            await PairSend(pending, PairHello()); await PairSend(pending, PairFinished());
            var task = PairSend(pending, PhoneSeal(PairProvision(), 1));
            if (replaceHello) await PairSend(pending, PairHello()); else pending.Dispose();
            wait.SetResult(true); await task;
            Check(!committed && verification.IsCancellationRequested, replaceHello ? "BLE replacement hello cancels pending verification and cannot commit old tokens" : "BLE cancellation prevents late authorization commit");
        }
        foreach (bool storageFail in new[] { false, true })
        {
            bool accepted = false; var packets = new MuseBlePackets(); var responses = new List<JObject>();
            using var reject = new MusePairingController(PairSession(), PairToken, () => true, p => { var raw = packets.Feed(p); if (raw != null) responses.Add(JObject.Parse(Encoding.UTF8.GetString(raw))); },
                (_, __) => storageFail ? Task.CompletedTask : Task.FromException(new Exception("fixture")),
                _ => { throw new Exception("fixture"); }, _ => accepted = true, _ => { }, _ => { });
            await PairSend(reject, PairHello()); await PairSend(reject, PairFinished()); await PairSend(reject, PhoneSeal(PairProvision(), 1));
            Check(!accepted && !responses.Where(v => (string)v["type"] == "pairing_encrypted").Select(PhoneOpen).Any(v => (string)v["status"] == "auth_ok"),
                storageFail ? "BLE encrypted storage failure never reports binding success" : "BLE rejected Muse authorization never reports binding success");
        }
        bool plainSaved = false;
        using var plaintext = new MusePairingController(PairSession(), PairToken, () => true, _ => { }, (_, __) => Task.CompletedTask,
            _ => plainSaved = true, _ => plainSaved = true, _ => { }, _ => { });
        await PairSend(plaintext, PairProvision()); Check(!plainSaved, "BLE plaintext credentials are never accepted");
        var probePackets = new MuseBlePackets(); var probeReplies = new List<JObject>();
        string probeFailure = null; bool probeTouchedCredentials = false;
        using var probe = new MusePairingController(PairSession(), null, () => true,
            p => { var raw = probePackets.Feed(p); if (raw != null) probeReplies.Add(JObject.Parse(Encoding.UTF8.GetString(raw))); },
            (_, __) => { probeTouchedCredentials = true; return Task.CompletedTask; },
            _ => probeTouchedCredentials = true, _ => probeTouchedCredentials = true, _ => { }, code => probeFailure = code, true);
        await PairSend(probe, new JObject { ["action"] = "get_device_info" });
        Check(probeReplies.Count == 1 && (string)probeReplies[0]["type"] == "device_info" && probeFailure == null,
            "tokenless discovery provides phone with public device metadata");
        await PairSend(probe, PairHello()); await PairSend(probe, PairProvision());
        Check(probeFailure == "PAIR_SDK_TOKEN_REQUIRED" && !probeTouchedCredentials && probeReplies.Count == 1,
            "tokenless discovery stops before authorization and never verifies or saves credentials");
    }
    private static HttpResponseMessage JsonResponse(string json) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> call;
        public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> call) { this.call = call; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => call(request);
    }
    private static async Task<byte[]> Read(MuseResponse response, CancellationToken cancellation = default)
    {
        using var stream = new MemoryStream(); byte[] chunk;
        while ((chunk = await response.Body.ReadAsync(cancellation)) != null) stream.Write(chunk, 0, chunk.Length);
        return stream.ToArray();
    }
    private static async Task Interop(int port)
    {
        using var stop = new CancellationTokenSource(20000);
        using var connection = await MuseNoiseConnection.ConnectLoopbackFixtureAsync(new Uri("ws://127.0.0.1:" + port), stop.Token);
        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index => {
            using var response = await connection.OpenAsync("/echo", Encoding.UTF8.GetBytes("{\"index\":" + index + ",\"text\":\"你好\"}"), stop.Token);
            return JObject.Parse(Encoding.UTF8.GetString(await Read(response, stop.Token)));
        }));
        Check(replies.Select(v => (int)v["index"]).SequenceEqual(Enumerable.Range(0, 8)), "real Unity ClientWebSocket multiplexes 8 streams against original Python Noise responder");
        using var large = await connection.OpenAsync("/split", Encoding.UTF8.GetBytes("{}"), stop.Token);
        Check((await Read(large, stop.Token)).SequenceEqual(Enumerable.Repeat((byte)'x', 180000)), "live cross-language transport handles out-of-order encrypted 180 KB response");
        using var cancel = new CancellationTokenSource(100);
        await Reject(connection.OpenAsync("/noheaders", Encoding.UTF8.GetBytes("{}"), cancel.Token), "headers cancellation sends reset and releases pending request");
        using var slowCancel = new CancellationTokenSource(); using var slow = await connection.OpenAsync("/slow", Encoding.UTF8.GetBytes("{}"), slowCancel.Token);
        var waiting = Read(slow, slowCancel.Token); slowCancel.Cancel(); await Reject(waiting, "body cancellation wakes pending reader");
        await Reject(connection.OpenAsync("/reset", Encoding.UTF8.GetBytes("{}"), stop.Token), "upstream stream reset is reported");
        using var healthy = await connection.OpenAsync("/echo", Encoding.UTF8.GetBytes("{}"), stop.Token);
        Check(healthy.Status == 200, "connection remains usable after stream cancellation/reset");
        using var live = await connection.OpenAsync("/slow", Encoding.UTF8.GetBytes("{}"), stop.Token);
        var liveRead = Read(live, stop.Token);
        await Reject(connection.OpenAsync("/disconnect", Encoding.UTF8.GetBytes("{}"), stop.Token), "WebSocket disconnect fails outstanding request");
        await Reject(liveRead, "WebSocket disconnect releases other live body readers");
    }
    private sealed class FakeMuse : IMuseChatClient
    {
        public bool IsConnected { get; private set; }
        public string VmName => "Fixture Muse";
        public int Sends;
        public string LastText;
        public Action<JObject> Receive;
        public Task ConnectAsync(CancellationToken cancellation) { IsConnected = true; return Task.CompletedTask; }
        public bool SubscriptionReady;
        public TaskCompletionSource<MuseChatAcknowledgement> PendingAck;
        public Task<MuseChatAcknowledgement> SendMessageAsync(string text, string sessionId, CancellationToken cancellation)
        {
            Check(SubscriptionReady, "reply subscription is ready before submitting recognized text");
            Check(sessionId == null, "new voice messages use existing main conversation without subscribing to an uncreated side chat");
            Sends++; LastText = text;
            return PendingAck?.Task ?? Task.FromResult(new MuseChatAcknowledgement("user", "canonical-user"));
        }
        public async Task SubscribeAsync(string sessionId, Action<JObject> onEvent, CancellationToken cancellation, Action onReady = null)
        { Check(sessionId == null, "reply subscription uses existing main conversation"); Receive = onEvent; SubscriptionReady = true; onReady?.Invoke(); try { await Task.Delay(Timeout.Infinite, cancellation); } finally { SubscriptionReady = false; } }
        public void Dispose() { IsConnected = false; }
    }
    private static async Task Until(Func<bool> predicate)
    { double end = EditorApplication.timeSinceStartup + 5; while (!predicate()) { if (EditorApplication.timeSinceStartup > end) throw new TimeoutException(); await Task.Delay(30); } }
    private static System.Collections.IEnumerator DelayedMicrophonePermission()
    { yield return null; yield return null; }
    private static async void RuntimeChecks()
    {
        report.Clear(); report.AddRange(File.ReadAllLines("edit-report.txt"));
        try
        {
            var button = UnityEngine.Object.FindFirstObjectByType<MuseVoiceButton>();
            Check(button != null && button.State == MuseVoiceButton.VoiceState.NeedsSetup && !button.IsConnected, "loading object does not log in, open microphone or send messages");
            var pairing = button.GetComponent<MuseBlePairing>();
            Check(pairing != null && !pairing.IsPairing, "loading optional component never automatically opens Bluetooth pairing");
            var view = button.GetComponentInChildren<MuseObjectView>(); var canvas = view.GetComponent<Canvas>();
            Check(canvas.GetComponent<UnityEngine.UI.GraphicRaycaster>() != null && UnityEngine.Object.FindObjectsByType<UnityEngine.EventSystems.EventSystem>(FindObjectsSortMode.None).Length == 1, "independent button has raycaster and exactly one EventSystem");
            var action = view.GetComponentsInChildren<UnityEngine.UI.Button>().First(v => v.name == "SpeakButton");
            Check(action.GetComponent<RectTransform>().rect.height >= 60, "standalone record/send touch target has nonzero size");
            Check(button.GetComponentInChildren<UnityEngine.UI.ScrollRect>().viewport != null, "reply scroll view has viewport and content");
            var bindButton = view.GetComponentsInChildren<UnityEngine.UI.Button>().Single(v => v.name == "BindMuseButton");
            bindButton.onClick.Invoke();
            Check(view.GetComponentsInChildren<TMPro.TMP_InputField>().Any(v => v.name == "SdkToken") && view.GetComponentsInChildren<TMPro.TMP_Text>().Any(v => v.name == "PairingSteps" && v.text.Contains("Add Device")),
                "Bind Muse button opens personal SDK token form and phone pairing steps");
            Canvas.ForceUpdateCanvases(); Capture(canvas, "preview-binding-setup.png");
            view.GetComponentsInChildren<UnityEngine.UI.Button>().Single(v => v.name == "Back").onClick.Invoke();
            var mock = new FakeMuse(); button.ClientFactory = (_, __) => mock;
            MuseCredentials savedConfiguration = null;
            button.PersistCredentials = value => savedConfiguration = MuseCredentials.Parse(MuseDeviceStore.Json(value));
            button.Transcribe = (_, __, ___) => Task.FromResult("recognized speech only");
            button.Configure(new MuseCredentials { AccessToken = "fixture", DeviceId = "fixture" }, "fixture", persist: true);
            Check(savedConfiguration.AsrKey == "fixture" && button.HasAsrKey, "saving optional object configuration includes usable ASR key");
            action.onClick.Invoke(); await Until(() => button.IsConnected);
            button.PrepareMicrophone = DelayedMicrophonePermission;
            int deviceLookups = 0;
            button.MicrophoneDevices = () => { deviceLookups++; return Array.Empty<string>(); };
            button.Press();
            Check(button.IsOpeningMicrophone && !action.interactable && action.GetComponentInChildren<TMPro.TMP_Text>().text == "Opening microphone…",
                "opening microphone disables send and distinguishes startup from actual recording");
            await Until(() => button.State == MuseVoiceButton.VoiceState.Error);
            Check(deviceLookups == 1 && !button.IsOpeningMicrophone && button.Status == "No microphone available." && mock.Sends == 0,
                "recording coroutine survives permission yields and finishes startup instead of cancelling itself");
            button.Press(); button.Cancel();
            await Task.Delay(100);
            Check(!button.IsOpeningMicrophone && deviceLookups == 1 && button.State == MuseVoiceButton.VoiceState.Ready && mock.Sends == 0,
                "Stop during microphone startup prevents delayed microphone acquisition and sends nothing");
            button.PrepareMicrophone = null; button.MicrophoneDevices = null;
            var sendButton = view.GetComponentsInChildren<UnityEngine.UI.Button>().Single(v => v.name == "SendButton");
            var recordedClip = AudioClip.Create("fixture-recorded-audio", 320000, 1, 16000, false);
            Action<string, object> field = (name, value) => typeof(MuseVoiceButton).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(button, value);
            field("recording", recordedClip); field("recordingStarted", Time.realtimeSinceStartup - 21f); field("lastRecordedFrames", 10000);
            typeof(MuseVoiceButton).GetMethod("Set", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(button,
                new object[] { MuseVoiceButton.VoiceState.Recording, "Listening fixture" });
            await Until(() => button.State == MuseVoiceButton.VoiceState.Recorded);
            Check(button.CanSendRecording && sendButton.interactable && !action.interactable && mock.Sends == 0 && button.RecordedSeconds == 20,
                "recording time limit retains audio with separate Send enabled and never automatically transcribes or sends");
            action.onClick.Invoke();
            Check(button.State == MuseVoiceButton.VoiceState.Recorded && mock.Sends == 0,
                "repeated Record activation cannot stop or send pending audio");
            int recordedAsrCalls = 0;
            button.Transcribe = (audio, __, ___) => { recordedAsrCalls++; Check(audio.Length == 640044, "manual Send reads the retained full recording"); return Task.FromException<string>(new MuseException("ASR_EMPTY_TRANSCRIPT")); };
            sendButton.onClick.Invoke(); await Until(() => button.State == MuseVoiceButton.VoiceState.Error);
            Check(recordedAsrCalls == 1 && mock.Sends == 0 && !button.CanSendRecording,
                "only explicit Send triggers ASR once and empty transcript never reaches Muse");
            button.Transcribe = (_, __, ___) => Task.FromResult("recognized speech only"); button.Cancel();
            foreach (var text in view.GetComponentsInChildren<TMPro.TMP_Text>())
            {
                text.ForceMeshUpdate();
                Check(text.fontSharedMaterial != null && text.fontSharedMaterial.shader != null && text.textInfo.characterCount > 0,
                    "visible text has font/shader/mesh: " + text.name);
            }
            mock.PendingAck = new TaskCompletionSource<MuseChatAcknowledgement>();
            var sending = button.SendAudioAsync(new byte[] { 1, 2 }); await Until(() => mock.Sends == 1);
            mock.Receive(JObject.Parse("{\"event\":\"delta.text_append\",\"payload\":{\"message_id\":\"reply\",\"reply_to_message_id\":\"canonical-user\",\"text\":\"Early \"}}"));
            await Task.Delay(70);
            Check(button.Reply == "", "events before the message acknowledgement remain buffered");
            mock.PendingAck.SetResult(new MuseChatAcknowledgement("user", "canonical-user"));
            await Until(() => button.Reply == "Early ");
            Check(button.State == MuseVoiceButton.VoiceState.Receiving, "early live reply is retained across acknowledgement ordering");
            Check(mock.Sends == 1 && mock.LastText == "recognized speech only" && button.Transcript == "recognized speech only", "independent button sends ASR result once through Muse interface");
            mock.Receive(JObject.Parse("{\"event\":\"delta.text_append\",\"payload\":{\"message_id\":\"reply\",\"text\":\"Hello from Muse\"}}"));
            await Until(() => button.Reply == "Early Hello from Muse");
            Check(button.State == MuseVoiceButton.VoiceState.Receiving, "network callback is marshalled onto Unity Update for partial reply");
            mock.Receive(JObject.Parse("{\"event\":\"delta.message_done\",\"payload\":{\"message_id\":\"reply\",\"reply_to_message_id\":\"canonical-user\",\"display_text\":\"Hello from Muse!\"}}"));
            await Until(() => button.State == MuseVoiceButton.VoiceState.ReplyReceived);
            Check(!button.IsBusy && button.Reply == "Hello from Muse!", "message ready enables next voice input without claiming agent-turn completion");
            Capture(canvas);
            button.Cancel(); await sending;
            mock.Receive(JObject.Parse("{\"event\":\"delta.text_append\",\"payload\":{\"message_id\":\"late\",\"text\":\"stale\"}}"));
            await Task.Delay(50); Check(!button.Reply.Contains("stale"), "cancel rejects late network callbacks");
            button.Transcribe = (_, __, ___) => Task.FromException<string>(new MuseException("ASR_EMPTY_TRANSCRIPT"));
            await button.SendAudioAsync(new byte[] { 1, 2 }); Check(mock.Sends == 1 && button.State == MuseVoiceButton.VoiceState.Error, "ASR failure does not send fallback or raw audio to Muse");
            button.enabled = false; Check(!mock.IsConnected, "disabling optional object closes Muse session");
            await Task.Delay(50); // Let Unity finish deferred view destruction before re-enabling.
            button.enabled = true; view = button.GetComponentInChildren<MuseObjectView>(); canvas = view.GetComponent<Canvas>();
            var ble = new FakeBle(); var newMuse = new FakeMuse(); MuseCredentials committed = null;
            pairing.PeripheralFactory = () => ble; pairing.SessionFactory = () => PairSession(); pairing.NetworkReady = () => true;
            pairing.VerifyCredentials = (_, __) => Task.CompletedTask; pairing.SaveCredentials = c => committed = c;
            button.ClientFactory = (_, __) => newMuse;
            button.BeginBluetoothBinding("invalid", "fixture");
            Check(!pairing.IsPairing && ble.Starts == 0, "invalid SDK token does not open BLE advertising");
            bindButton = view.GetComponentsInChildren<UnityEngine.UI.Button>().Single(v => v.name == "BindMuseButton");
            bindButton.onClick.Invoke();
            view.GetComponentsInChildren<UnityEngine.UI.Button>().Single(v => v.name == "DiscoveryButton").onClick.Invoke();
            await Until(() => ble.Starts == 1); ble.Event("advertising", ble.Name);
            await Until(() => button.Status.Contains("Scan test only"));
            Check(button.IsPairing && ble.Name == "MuseGadget000001", "Scan test button advertises without a personal SDK token");
            ble.Phone(new JObject { ["action"] = "get_device_info" });
            await Until(() => ble.Messages.Count == 1); ble.Phone(PairHello());
            await Until(() => !pairing.IsPairing);
            Check(ble.Disposed && committed == null && button.Status.Contains("Device discovery succeeded"),
                "phone discovery ends with explicit SDK requirement and no credentials committed");
            ble = new FakeBle(); pairing.PeripheralFactory = () => ble;
            button.BeginBluetoothBinding(PairToken, "fixture"); await Until(() => ble.Starts == 1);
            ble.Event("advertising", ble.Name); await Until(() => button.Status.Contains(ble.Name));
            Check(button.IsBusy && button.IsPairing && ble.Name == "MuseGadget000001" && button.Transcript == "" && button.Reply == "",
                "binding locks voice input, clears previous conversation and shows discoverable MuseGadget name");
            Capture(canvas, "preview-binding-fixture.png");
            ble.Phone(PairHello()); ble.Phone(PairFinished()); ble.Phone(PhoneSeal(PairProvision(), 1));
            await Until(() => button.IsConnected && !pairing.IsPairing);
            Check(committed != null && committed.DeviceId == "homelink-000001" && ble.Finished && ble.Disposed && newMuse.Sends == 0 &&
                (string)PhoneOpen(ble.Messages.Last())["status"] == "auth_ok",
                "BLE binding saves verified credentials, requests auth_ok delivery, closes fixture peripheral and connects without sending speech");
            Check(pairing.DiagnosticSummary.Contains("provision_v2") && pairing.DiagnosticSummary.Contains("credentials_saved") &&
                !pairing.DiagnosticSummary.Contains(PairToken) && !pairing.DiagnosticSummary.Contains((string)PairProvision()["access_token"]),
                "BLE diagnostics include protocol phases and packet lengths without SDK or device tokens");
            var cancelled = new FakeBle(); var pending = new TaskCompletionSource<bool>(); bool lateSaved = false;
            pairing.PeripheralFactory = () => cancelled; pairing.VerifyCredentials = (_, __) => pending.Task; pairing.SaveCredentials = _ => lateSaved = true;
            button.BeginBluetoothBinding(PairToken, "fixture"); await Until(() => cancelled.Starts == 1);
            cancelled.Phone(PairHello()); cancelled.Phone(PairFinished()); cancelled.Phone(PhoneSeal(PairProvision(), 1));
            await Until(() => button.Status.Contains("Checking Muse")); button.Cancel(); pending.SetResult(true); await Task.Delay(100);
            Check(!lateSaved && !pairing.IsPairing && cancelled.Disposed && !button.IsConnected, "Stop during Bluetooth authorization closes peripheral and discards late credentials");
            report.Add("BOUNDARY: Python peer, BLE peripheral and UI services are isolated fixtures. No real Muse account, ASR service, radio, phone App or Android Keystore was exercised.");
            File.WriteAllLines("report.txt", report); EditorApplication.Exit(0);
        }
        catch (Exception error) { File.WriteAllLines("report.txt", report.Concat(new[] { "ERROR " + error })); EditorApplication.Exit(1); }
    }
    private sealed class FakeBle : IMuseBlePeripheral
    {
        public Action<string, string> Event;
        public int Starts; public string Name; public bool Finished, Disposed;
        private readonly MuseBlePackets assembler = new MuseBlePackets();
        public readonly List<JObject> Messages = new List<JObject>();
        public void Start(string name, Action<string, string> callback) { Starts++; Name = name; Event = callback; }
        public void Send(byte[] packet) { var raw = assembler.Feed(packet); if (raw != null) Messages.Add(JObject.Parse(Encoding.UTF8.GetString(raw))); }
        public void Complete() { Finished = true; Event("completed", ""); }
        public void Dispose() { Disposed = true; }
        public void Phone(JObject command) { foreach (var packet in MuseBlePackets.Encode(command)) Event("write", Convert.ToBase64String(packet)); }
    }
    private static void Capture(Canvas canvas, string path = "preview-fixture.png")
    {
        var camera = UnityEngine.Object.FindFirstObjectByType<Camera>(); var target = RenderTexture.GetTemporary(1280, 800, 24);
        var previousTarget = camera.targetTexture; var previousActive = RenderTexture.active; var mode = canvas.renderMode;
        var texture = new Texture2D(1280, 800, TextureFormat.RGB24, false);
        try
        {
            canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = 1;
            camera.targetTexture = target; Canvas.ForceUpdateCanvases(); camera.Render(); RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, 1280, 800), 0, 0); texture.Apply(); File.WriteAllBytes(path, texture.EncodeToPNG());
        }
        finally { canvas.renderMode = mode; camera.targetTexture = previousTarget; RenderTexture.active = previousActive; RenderTexture.ReleaseTemporary(target); UnityEngine.Object.Destroy(texture); }
    }
}
