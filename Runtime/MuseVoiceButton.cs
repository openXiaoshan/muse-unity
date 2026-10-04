using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace Muse.Unity
{
    [DisallowMultipleComponent]
    public sealed class MuseVoiceButton : MonoBehaviour
    {
        public enum VoiceState { NeedsSetup, Ready, Connecting, Recording, Recorded, Transcribing, Sending, Awaiting, Receiving, ReplyReceived, Error }
        [SerializeField, Range(2, 30)] private int maxRecordingSeconds = 20;
        [SerializeField] private bool buildViewOnEnable = true;
        // Secret values are runtime-only. Optional object loading does not activate or connect it.
        private MuseCredentials credentials;
        private Action<MuseCredentials> saveCredentials;
        private string asrKey;
        private IMuseChatClient client;
        // Runtime dependency injection supports isolated lifecycle tests and alternative ASR providers.
        public Func<MuseCredentials, Action<MuseCredentials>, IMuseChatClient> ClientFactory;
        public Func<byte[], string, CancellationToken, Task<string>> Transcribe;
        public Action<MuseCredentials> PersistCredentials;
        // Runtime fixtures can delay permission and report no devices without opening the real microphone.
        public Func<IEnumerator> PrepareMicrophone;
        public Func<string[]> MicrophoneDevices;
        private MuseObjectView view;
        private MuseBlePairing pairing;
        private bool showPairingStatus;
        private CancellationTokenSource work;
        private AudioClip recording;
        private string microphone;
        private float recordingStarted;
        private int lastRecordedFrames;
        private bool acquiringMicrophone;
        private int generation;
        private float captureRequested;
        private readonly System.Collections.Generic.Queue<string> microphoneDiagnostic = new System.Collections.Generic.Queue<string>();
        public string MicrophoneDiagnosticSummary => string.Join("\n", microphoneDiagnostic);
        private readonly ConcurrentQueue<string> chatDiagnostic = new ConcurrentQueue<string>();
        public string ChatDiagnosticSummary => string.Join("\n", chatDiagnostic.ToArray());
        private void RecordChat(string entry)
        {
            chatDiagnostic.Enqueue(entry); while (chatDiagnostic.Count > 128) chatDiagnostic.TryDequeue(out _);
#if UNITY_EDITOR
            Debug.Log("[Muse CHAT] " + entry);
#endif
        }
        private readonly ConcurrentQueue<Action> updates = new ConcurrentQueue<Action>();
        public VoiceState State { get; private set; } = VoiceState.NeedsSetup;
        private string status = "Bind Muse or import paired credentials in Setup.";
        public string Status => showPairingStatus && pairing != null ? pairing.Status : status;
        public bool IsPairing => pairing != null && pairing.IsPairing;
        public string Transcript { get; private set; } = "";
        public string Reply { get; private set; } = "";
        public bool IsConnected => client?.IsConnected == true;
        public bool HasAsrKey => !string.IsNullOrWhiteSpace(asrKey);
        public bool IsOpeningMicrophone => acquiringMicrophone;
        public bool CanSendRecording => recording != null && !acquiringMicrophone && (State == VoiceState.Recording || State == VoiceState.Recorded);
        public float RecordedSeconds => recording == null ? 0 : lastRecordedFrames / (float)recording.frequency;
        public bool IsBusy => IsPairing || acquiringMicrophone || State == VoiceState.Connecting || State == VoiceState.Recording || State == VoiceState.Transcribing ||
            State == VoiceState.Sending || State == VoiceState.Awaiting || State == VoiceState.Receiving;
        public event Action<string> OnTranscript;
        public event Action<string> OnReply;
        public event Action Changed;

        public void Configure(MuseCredentials museCredentials, string elevenLabsAsrKey, Action<MuseCredentials> onRotatedCredentials = null, bool persist = false)
        {
            string resolved = MuseAsrConfiguration.Resolve(elevenLabsAsrKey, museCredentials.AsrKey);
            museCredentials.AsrKey = resolved;
            if (persist) (PersistCredentials ?? MuseDeviceStore.Save)(museCredentials);
            Disconnect(); credentials = museCredentials; asrKey = resolved; saveCredentials = onRotatedCredentials;
            Transcript = Reply = "";
            Set(VoiceState.Ready, "Ready to connect to Muse.");
        }
        public void BeginBluetoothBinding(string sdkToken, string elevenLabsAsrKey)
        {
            Disconnect(); asrKey = MuseAsrConfiguration.Resolve(elevenLabsAsrKey, credentials?.AsrKey);
            Transcript = Reply = "";
            showPairingStatus = true; pairing.Begin(sdkToken?.Trim()); Changed?.Invoke();
        }
        public void BeginBluetoothDiscovery()
        { Disconnect(); Transcript = Reply = ""; showPairingStatus = true; pairing.Begin(null, true); Changed?.Invoke(); }
        private void PairingChanged() => Changed?.Invoke();
        private void PairingBound(MuseCredentials value)
        { try { Configure(value, asrKey, persist: true); Connect(); } catch (Exception e) { Error(e); } }
        private void OnEnable()
        {
            if (!Application.isPlaying) return;
            MuseDeviceStore.Initialize();
            pairing = GetComponent<MuseBlePairing>() ?? gameObject.AddComponent<MuseBlePairing>();
            pairing.Changed += PairingChanged; pairing.Bound += PairingBound;
            if (credentials == null)
            {
                try
                {
                    credentials = MuseDeviceStore.Load();
                    if (credentials != null)
                    {
                        asrKey = MuseAsrConfiguration.Resolve(asrKey, credentials.AsrKey);
                        if (credentials.AsrKey != asrKey) { credentials.AsrKey = asrKey; (PersistCredentials ?? MuseDeviceStore.Save)(credentials); }
                        Set(VoiceState.Ready, "Saved Muse binding loaded. Tap Connect Muse.");
                    }
                }
                catch { Set(VoiceState.NeedsSetup, "Saved Muse binding could not be read. Bind Muse again."); }
            }
            if (asrKey == null) asrKey = MuseAsrConfiguration.Resolve(null);
            if (buildViewOnEnable && view == null) view = MuseObjectView.Create(this);
        }
        private void Update()
        {
            while (updates.TryDequeue(out var action)) action();
            if (recording != null && !acquiringMicrophone) lastRecordedFrames = Math.Max(lastRecordedFrames, Microphone.GetPosition(microphone));
            if (recording != null && !acquiringMicrophone && State == VoiceState.Recording)
            {
                if (Time.realtimeSinceStartup - recordingStarted >= maxRecordingSeconds) FinishRecording("duration_limit");
                else if (!Microphone.IsRecording(microphone)) FinishRecording("capture_ended");
            }
            if (IsConnected == false && !IsBusy && State != VoiceState.NeedsSetup && State != VoiceState.Ready && State != VoiceState.Error)
                Set(VoiceState.Error, "Muse disconnected. Reconnect before speaking.");
        }
        public void Press()
        {
            RecordMicrophone("press");
            // Record is start-only. Sending requires the separate Send button, so repeated
            // pointer activation cannot immediately stop a newly started recording.
            if (acquiringMicrophone || State == VoiceState.Recording || State == VoiceState.Recorded) return;
            if (IsBusy) return;
            if (!IsConnected) { Connect(); return; }
            // Stop previous work before starting the coroutine. Calling Cancel inside it would
            // StopAllCoroutines on its own first yield and leave the opening state stuck forever.
            Cancel();
            microphoneDiagnostic.Clear(); captureRequested = Time.realtimeSinceStartup;
            RecordMicrophone("start_requested");
            StartCoroutine(StartRecording());
        }
        public async void Connect()
        {
            if (IsBusy) return;
            if (credentials == null) { Set(VoiceState.NeedsSetup, "Import credentials from a Muse-paired device first."); return; }
            Cancel(); client?.Dispose();
            work = new CancellationTokenSource(); int current = generation;
            Action<MuseCredentials> rotated = next => { next.AsrKey = asrKey; (PersistCredentials ?? MuseDeviceStore.Save)(next); credentials = next; saveCredentials?.Invoke(next); };
            client = ClientFactory != null ? ClientFactory(credentials, rotated) : new MuseClient(credentials, rotated);
            Set(VoiceState.Connecting, "Connecting to Muse…");
            try
            {
                await client.ConnectAsync(work.Token);
                if (Current(current)) Set(VoiceState.Ready, "Connected to " + client.VmName + ". Tap to speak.");
            }
            catch (Exception e) { if (Current(current)) Error(e); }
        }
        private IEnumerator StartRecording()
        {
            if (string.IsNullOrWhiteSpace(asrKey)) { Set(VoiceState.Error, "Enter your ElevenLabs ASR key in Setup."); yield break; }
            int current = generation; acquiringMicrophone = true;
            Set(VoiceState.Recording, "Opening microphone…");
            if (PrepareMicrophone != null) yield return PrepareMicrophone();
            else yield return WaitForMicrophonePermission(current);
            if (!Current(current)) yield break;
            if (State == VoiceState.Error) { acquiringMicrophone = false; yield break; }
            string[] devices = MicrophoneDevices != null ? MicrophoneDevices() : Microphone.devices;
            if (devices.Length == 0) { acquiringMicrophone = false; Set(VoiceState.Error, "No microphone available."); yield break; }
            microphone = devices[0];
            if (Microphone.IsRecording(microphone)) { acquiringMicrophone = false; Set(VoiceState.Error, "Microphone is in use. Stop the other recording first."); yield break; }
            try { recording = Microphone.Start(microphone, false, maxRecordingSeconds, 16000); }
            catch { acquiringMicrophone = false; Set(VoiceState.Error, "Unable to start microphone."); yield break; }
            if (recording == null) { acquiringMicrophone = false; Set(VoiceState.Error, "Unable to start microphone."); yield break; }
            float deadline = Time.realtimeSinceStartup + 5;
            while (Current(current) && Microphone.GetPosition(microphone) <= 0 && Time.realtimeSinceStartup < deadline) yield return null;
            if (!Current(current)) yield break;
            acquiringMicrophone = false;
            if (Microphone.GetPosition(microphone) <= 0) { ReleaseRecording(); Set(VoiceState.Error, "Microphone did not produce audio."); yield break; }
            recordingStarted = Time.realtimeSinceStartup; lastRecordedFrames = Microphone.GetPosition(microphone); Transcript = Reply = "";
            RecordMicrophone("listening");
            Set(VoiceState.Recording, "Listening… Speak, then tap Send.");
        }
        private IEnumerator WaitForMicrophonePermission(int current)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                var callbacks = new UnityEngine.Android.PermissionCallbacks(); bool answered = false;
                callbacks.PermissionGranted += _ => answered = true; callbacks.PermissionDenied += _ => answered = true;
                callbacks.PermissionDeniedAndDontAskAgain += _ => answered = true;
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone, callbacks);
                float permissionEnd = Time.realtimeSinceStartup + 60;
                while (!answered && Current(current) && Time.realtimeSinceStartup < permissionEnd) yield return null;
            }
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            { acquiringMicrophone = false; Set(VoiceState.Error, "Microphone permission required."); yield break; }
#else
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                Set(VoiceState.Recording, "Allow microphone access in the system prompt…");
                var request = Application.RequestUserAuthorization(UserAuthorization.Microphone);
                float permissionEnd = Time.realtimeSinceStartup + 60;
                while (!request.isDone && Current(current) && Time.realtimeSinceStartup < permissionEnd) yield return null;
                if (!Current(current)) yield break;
                if (!request.isDone) { Set(VoiceState.Error, "Microphone permission timed out. Check system microphone settings, then retry."); yield break; }
            }
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            { acquiringMicrophone = false; Set(VoiceState.Error, "Microphone permission required."); yield break; }
#endif
        }
        public void StopRecordingAndSend()
            => StopRecordingAndSend("manual_send");
        private void FinishRecording(string reason)
        {
            RecordMicrophone(reason);
            int position = Microphone.GetPosition(microphone);
            lastRecordedFrames = reason == "duration_limit" ? recording.samples : Math.Max(lastRecordedFrames, position);
            Microphone.End(microphone);
            if (lastRecordedFrames < recording.frequency / 5)
            { ReleaseRecording(); Set(VoiceState.Error, "Microphone stopped before capturing enough audio. Tap Record to retry."); return; }
            Set(VoiceState.Recorded, "Recording ready. Tap Send, or Stop to discard.");
        }
        private void StopRecordingAndSend(string reason)
        {
            if (recording == null || acquiringMicrophone) return;
            RecordMicrophone(reason);
            int frames = State == VoiceState.Recorded ? lastRecordedFrames : Microphone.GetPosition(microphone);
            if (frames <= 0 && !Microphone.IsRecording(microphone))
                frames = Time.realtimeSinceStartup - recordingStarted >= maxRecordingSeconds - .15f ? recording.samples : lastRecordedFrames;
            frames = Mathf.Clamp(frames, 0, recording.samples);
            if (frames < recording.frequency / 5) { ReleaseRecording(); Set(VoiceState.Ready, "Recording too short. Tap to try again."); return; }
            var samples = new float[frames * recording.channels];
            if (!recording.GetData(samples, 0)) { ReleaseRecording(); Set(VoiceState.Error, "Unable to read microphone audio."); return; }
            byte[] wav = MuseAsr.EncodeWav(samples, recording.channels, recording.frequency); Array.Clear(samples, 0, samples.Length);
            ReleaseRecording(); _ = SendAudioAsync(wav);
        }
        // Public audio input also supports another recorder with an independent ASR provider.
        public async Task SendAudioAsync(byte[] wav)
        {
            if (!IsConnected || IsBusy && State != VoiceState.Recording) { Set(VoiceState.Error, "Connect to Muse before recording."); return; }
            work?.Cancel(); work?.Dispose(); work = new CancellationTokenSource();
            int current = ++generation; var token = work.Token;
            while (chatDiagnostic.TryDequeue(out _)) { }
            RecordChat("asr_started");
            Set(VoiceState.Transcribing, "Recognizing speech…");
            try
            {
                string text = Transcribe != null ? await Transcribe(wav, asrKey, token) : await MuseAsr.TranscribeAsync(wav, asrKey, token);
                if (!Current(current)) return;
                RecordChat("asr_ready");
                Transcript = text; OnTranscript?.Invoke(text); Changed?.Invoke();
                Set(VoiceState.Sending, "Preparing Muse reply channel…");
                // Match the official Gadget's existing main conversation. A newly generated
                // side-chat ID cannot be subscribed until its first message has created it.
                string sessionId = null;
                using var responseDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                responseDeadline.CancelAfter(90000);
                var subscriptionReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var received = new ConcurrentQueue<JObject>();
                MuseReplyCollector collector = null;
                void DrainReplies()
                {
                    if (!Current(current) || collector == null) return;
                    while (received.TryDequeue(out var value))
                    {
                        bool accepted = collector.Receive(value);
                        RecordChat("reply_event;decision=" + collector.LastDecision);
                        if (!accepted) continue;
                        Reply = collector.Text; OnReply?.Invoke(Reply);
                        bool ready = collector.MessageReady;
                        Set(ready ? VoiceState.ReplyReceived : VoiceState.Receiving,
                            ready ? "Muse replied. Tap Record to speak again." : "Muse is replying…");
                        if (ready) responseDeadline.CancelAfter(Timeout.Infinite);
                    }
                }
                // Muse's event subscription is live, not replayed: open it before submitting text.
                // Buffer early events until the acknowledgement supplies the user-message ID.
                Task subscription = client.SubscribeAsync(sessionId, value => {
                    if (token.IsCancellationRequested || current != Volatile.Read(ref generation)) return;
                    string kind = (string)value["event"] ?? (string)value["event_name"];
                    switch (kind) {
                        case "delta.message_start": case "delta.text_append": case "delta.message_done":
                        case "message.user": case "message.assistant": break;
                        default: kind = "other"; break;
                    }
                    RecordChat("event_received;kind=" + kind);
                    received.Enqueue(value);
                    if (received.Count > 128) throw new MuseException("MUSE_REPLY_BUFFER_FULL");
                    updates.Enqueue(DrainReplies);
                }, responseDeadline.Token, () => { RecordChat("subscription_ready"); subscriptionReady.TrySetResult(true); });
                try
                {
                    await Task.WhenAny(subscriptionReady.Task, subscription);
                    if (subscription.IsCompleted) await subscription;
                    await subscriptionReady.Task;
                    if (!Current(current)) return;
                    Set(VoiceState.Sending, "Sending recognized text to Muse…");
                    var ack = await client.SendMessageAsync(text, sessionId, responseDeadline.Token);
                    RecordChat("message_accepted;has_parent=" + (!string.IsNullOrEmpty(ack.ParentMessageId) ? "1" : "0"));
                    if (!Current(current)) return;
                    collector = new MuseReplyCollector(sessionId, ack.MessageId, ack.ParentMessageId, scopedSubscription: false);
                    responseDeadline.CancelAfter(90000);
                    Set(VoiceState.Awaiting, "Sent. Waiting for Muse…");
                    DrainReplies();
                    await subscription;
                }
                finally
                {
                    responseDeadline.Cancel();
                    try { await subscription; } catch { /* The active failure is handled below. */ }
                    while (received.TryDequeue(out _)) { }
                }
            }
            catch (OperationCanceledException)
            { if (Current(current) && !token.IsCancellationRequested) { RecordChat("reply_timeout"); Set(VoiceState.Error, "Reply timed out; Muse may already have replied in the App. It was not resent."); } }
            catch (Exception e) { if (Current(current)) Error(e); }
            finally { if (wav != null) Array.Clear(wav, 0, wav.Length); }
        }
        public void Cancel()
        {
            RecordMicrophone("cancel");
            pairing?.Stop(); showPairingStatus = false;
            generation++; work?.Cancel(); work?.Dispose(); work = null;
            StopAllCoroutines(); acquiringMicrophone = false; ReleaseRecording();
            Set(credentials == null ? VoiceState.NeedsSetup : VoiceState.Ready, IsConnected ? "Connected to Muse. Tap Record to speak." : "Ready to connect to Muse.");
        }
        public void Disconnect() { Cancel(); client?.Dispose(); client = null; }
        private void OnDisable()
        { Disconnect(); if (pairing != null) { pairing.Changed -= PairingChanged; pairing.Bound -= PairingBound; } if (view != null) Destroy(view.gameObject); view = null; }
        private bool Current(int current) => this != null && isActiveAndEnabled && current == generation;
        private void ReleaseRecording()
        {
            if (recording == null) return;
            Microphone.End(microphone); Destroy(recording); recording = null; microphone = null;
        }
        private void Set(VoiceState state, string value) { showPairingStatus = false; State = state; status = value; Changed?.Invoke(); }
        private void RecordMicrophone(string phase)
        {
            int frames = recording == null ? 0 : Microphone.GetPosition(microphone);
            string entry = phase + ";elapsed_ms=" + Mathf.RoundToInt((Time.realtimeSinceStartup - captureRequested) * 1000) + ";frames=" + frames + ";limit_seconds=" + maxRecordingSeconds;
            while (microphoneDiagnostic.Count >= 64) microphoneDiagnostic.Dequeue(); microphoneDiagnostic.Enqueue(entry);
#if UNITY_EDITOR
            Debug.Log("[Muse MIC] " + entry);
#endif
        }
        private void Error(Exception error)
        {
            string code = error is MuseException muse ? muse.Code : error is OperationCanceledException ? "REQUEST_TIMEOUT" : "MUSE_REQUEST_FAILED";
            if (code == "ASR_EMPTY_TRANSCRIPT") { Set(VoiceState.Error, "No speech recognized. Tap Record, speak, then tap Send."); return; }
            int httpStatus = error is MuseException failure ? failure.Status : 0;
            RecordChat("request_error;http_status=" + httpStatus);
            Set(VoiceState.Error, code + (httpStatus > 0 ? " (HTTP " + httpStatus + ")" : "") + ". Check connection/setup before retrying; sent text is not automatically resent.");
        }
    }
}
