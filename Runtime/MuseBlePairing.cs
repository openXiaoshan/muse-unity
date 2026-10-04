using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Muse.Unity
{
    public interface IMuseBlePeripheral : IDisposable
    {
        void Start(string deviceName, Action<string, string> onEvent);
        void Send(byte[] packet);
        void Complete();
    }

    [DisallowMultipleComponent]
    public sealed class MuseBlePairing : MonoBehaviour
    {
        // Injection is runtime-only, for isolated protocol/lifecycle checks.
        public Func<IMuseBlePeripheral> PeripheralFactory;
        public Func<MuseCredentials, CancellationToken, Task> VerifyCredentials;
        public Action<MuseCredentials> SaveCredentials;
        public Func<MusePairingSession> SessionFactory;
        public Func<bool> NetworkReady;
        public bool IsPairing { get; private set; }
        public string Status { get; private set; } = "";
        public string DeviceName { get; private set; } = "";
        private readonly Queue<string> diagnostic = new Queue<string>();
        private int receivedPackets, receivedBytes, largestPacket;
        public string DiagnosticSummary => "RX packets=" + receivedPackets + "; bytes=" + receivedBytes + "; largest=" + largestPacket + "\n" + string.Join("\n", diagnostic);
        public event Action Changed;
        public event Action<MuseCredentials> Bound;
        private readonly ConcurrentQueue<Action> events = new ConcurrentQueue<Action>();
        private IMuseBlePeripheral peripheral;
        private MusePairingController controller;
        private MuseCredentials validated;
        private float deadline;
        private int generation;

        public void Begin(string sdkToken, bool discoveryOnly = false)
        {
            Stop();
            diagnostic.Clear(); receivedPackets = receivedBytes = largestPacket = 0;
            if (!discoveryOnly && (sdkToken == null || !Regex.IsMatch(sdkToken, "^mgst_[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]$"))) { Set("Enter a personal Muse SDK token in Setup."); return; }
#if !((UNITY_ANDROID && !UNITY_EDITOR) || UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX)
            if (PeripheralFactory == null) { Set("Bluetooth binding requires Android or macOS. Use credential import on this platform."); return; }
#endif
            IsPairing = true; deadline = Time.realtimeSinceStartup + 600; Set("Preparing Bluetooth binding…");
            StartCoroutine(Open(sdkToken, generation, discoveryOnly));
        }
        private IEnumerator Open(string token, int current, bool discoveryOnly)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (PeripheralFactory == null)
            {
                using var version = new AndroidJavaClass("android.os.Build$VERSION");
                if (version.GetStatic<int>("SDK_INT") >= 31)
                {
                    string[] required = { "android.permission.BLUETOOTH_ADVERTISE", "android.permission.BLUETOOTH_CONNECT" };
                    foreach (string permission in required)
                    {
                        if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission)) continue;
                        bool answered = false; var callbacks = new UnityEngine.Android.PermissionCallbacks();
                        callbacks.PermissionGranted += _ => answered = true; callbacks.PermissionDenied += _ => answered = true;
                        callbacks.PermissionDeniedAndDontAskAgain += _ => answered = true;
                        UnityEngine.Android.Permission.RequestUserPermission(permission, callbacks);
                        float until = Time.realtimeSinceStartup + 60;
                        while (!answered && Active(current) && Time.realtimeSinceStartup < until) yield return null;
                        if (!Active(current)) yield break;
                        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(permission)) { Fail("BLUETOOTH_PERMISSION_REQUIRED"); yield break; }
                    }
                }
            }
#endif
            if (!Active(current)) yield break;
            try
            {
                var session = SessionFactory != null ? SessionFactory() : new MusePairingSession(MuseDeviceStore.Identity());
                DeviceName = session.Identity.Name;
                peripheral = PeripheralFactory != null ? PeripheralFactory() : DefaultPeripheral();
                controller = new MusePairingController(session, token, NetworkReady ?? (() => Application.internetReachability != NetworkReachability.NotReachable),
                    packet => peripheral.Send(packet), VerifyCredentials ?? VerifyAsync,
                    credentials => { (SaveCredentials ?? MuseDeviceStore.Save)(credentials); validated = credentials; },
                    _ => { Set("Authorized. Finishing phone confirmation…"); peripheral.Complete(); }, Set, Fail, discoveryOnly, Record);
                peripheral.Start(DeviceName, (kind, value) => {
                    if (current != Volatile.Read(ref generation)) return;
                    if (events.Count >= 2048) return; // Bound memory if an untrusted nearby peer floods GATT.
                    events.Enqueue(() => { if (Active(current)) Receive(kind, value, discoveryOnly); });
                });
            }
            catch { Fail("BLE_START_FAILED"); }
        }
        private static async Task VerifyAsync(MuseCredentials credentials, CancellationToken cancellation)
        {
            // Verify the exact phone-issued access token, without rotating it during pairing.
            var copy = MuseCredentials.Parse(MuseDeviceStore.Json(credentials)); copy.RefreshToken = null;
            using var account = new MuseAccount(copy); var vms = await account.ListVmsAsync(cancellation);
            if (vms.Count == 0) throw new MuseException("MUSE_VM_UNAVAILABLE");
        }
        private static IMuseBlePeripheral DefaultPeripheral()
        {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            return new MuseMacPeripheral();
#else
            return new AndroidMusePeripheral();
#endif
        }
        private void Receive(string kind, string value, bool discoveryOnly)
        {
            if (kind == "advertising") Set(discoveryOnly ? "Discoverable: " + DeviceName + "\nScan test only. Add SDK token to bind." : "Waiting for Muse app: " + DeviceName + "\nSettings > Devices > Add Device.");
            else if (kind == "write")
            {
                try { if (value == null || value.Length > 11000) throw new MuseException("PAIR_PACKET_INVALID");
                    var packet = Convert.FromBase64String(value); receivedPackets++; receivedBytes += packet.Length; largestPacket = Math.Max(largestPacket, packet.Length);
                    Record("packet_bytes=" + packet.Length + (packet.Length >= 3 && packet[0] == 0xfe ? ";fragment=" + packet[1] + "/" + packet[2] : ";unframed"));
                    _ = controller.ReceiveAsync(packet); }
                catch { Fail("PAIR_PACKET_INVALID"); }
            }
            else if (kind == "connected") Record("phone_subscribed");
            else if (kind == "completed")
            {
                var credentials = validated;
                if (credentials == null) { Fail("PAIR_AUTH_MISSING"); return; }
                Stop(); Set("Muse bound. Tap Connect Muse."); Bound?.Invoke(credentials);
            }
            else if (kind == "error") Fail(value);
        }
        private void Update()
        {
            // Keep callbacks and Unity view updates on the main thread.
            for (int i = 0; i < 128 && events.TryDequeue(out var callback); i++) callback();
            if (IsPairing && Time.realtimeSinceStartup >= deadline) Fail("PAIR_WINDOW_EXPIRED");
        }
        private bool Active(int current) => this != null && isActiveAndEnabled && IsPairing && current == generation;
        private void Set(string status) { Status = status; Changed?.Invoke(); }
        private void Record(string phase)
        {
            if (!Regex.IsMatch(phase, "^[A-Za-z0-9_=;/]+$")) phase = "unknown_error";
            while (diagnostic.Count >= 64) diagnostic.Dequeue(); diagnostic.Enqueue(phase);
#if UNITY_EDITOR
            Debug.Log("[Muse BLE] " + phase);
#endif
        }
        private void Fail(string code)
        {
            Record(code);
            Stop();
            Set(code == "BLUETOOTH_OFF" ? "Turn on Bluetooth in system settings, then retry Bind Muse."
                : code == "BLE_PERIPHERAL_NOT_SUPPORTED" || code == "BLE_NOT_SUPPORTED" ? "This device does not support BLE peripheral advertising."
                : code == "BLUETOOTH_PERMISSION_REQUIRED" ? "Allow Bluetooth permission in system settings to bind Muse."
                : code == "PAIR_SDK_TOKEN_REQUIRED" ? "Device discovery succeeded. Add a personal SDK token in Bind Muse to authorize it."
                : "Binding stopped: " + code + ". Retry Bind Muse.");
        }
        public void Stop()
        {
            generation++; IsPairing = false; StopAllCoroutines(); controller?.Dispose(); controller = null;
            peripheral?.Dispose(); peripheral = null; validated = null;
            while (events.TryDequeue(out _)) { }
            Changed?.Invoke();
        }
        private void OnDisable() => Stop();

        private sealed class AndroidMusePeripheral : IMuseBlePeripheral
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            private AndroidJavaObject native;
            private Listener listener;
            private sealed class Listener : AndroidJavaProxy
            {
                private readonly Action<string, string> callback;
                public Listener(Action<string, string> callback) : base("io.github.openxiaoshan.muse.MuseBlePeripheral$Listener") { this.callback = callback; }
                [UnityEngine.Scripting.Preserve] public void onEvent(string kind, string value) => callback(kind, value);
            }
            public void Start(string name, Action<string, string> onEvent)
            {
                using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer"); using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
                listener = new Listener(onEvent); native = new AndroidJavaObject("io.github.openxiaoshan.muse.MuseBlePeripheral", activity, listener); native.Call("start", name);
            }
            public void Send(byte[] packet) => native.Call("send", Convert.ToBase64String(packet));
            public void Complete() => native.Call("complete");
            public void Dispose() { if (native != null) { native.Call("stop"); native.Dispose(); native = null; } listener = null; }
#else
            public void Start(string name, Action<string, string> onEvent) => throw new MuseException("PAIR_ANDROID_REQUIRED");
            public void Send(byte[] packet) { }
            public void Complete() { }
            public void Dispose() { }
#endif
        }
    }
}
