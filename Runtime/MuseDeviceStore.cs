using System;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Muse.Unity
{
    public static class MuseDeviceStore
    {
        private static int unityThread;
        public static void Initialize() => unityThread = Thread.CurrentThread.ManagedThreadId;
        public static string Json(MuseCredentials credentials) => new JObject {
            ["access_token"] = credentials.AccessToken, ["refresh_token"] = credentials.RefreshToken, ["device_id"] = credentials.DeviceId,
            ["sdk_token"] = credentials.SdkToken, ["api_url_v2"] = credentials.ApiUrl, ["noise_host"] = credentials.NoiseHost,
            ["access_token_saved_at"] = credentials.SavedAt, ["elevenlabs_asr_key"] = credentials.AsrKey }.ToString(Formatting.None);
#if UNITY_ANDROID && !UNITY_EDITOR
        private static T Call<T>(string method, params object[] args)
        {
            bool background = Thread.CurrentThread.ManagedThreadId != unityThread;
            if (background) AndroidJNI.AttachCurrentThread();
            try
            {
                using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
                using var store = new AndroidJavaClass("io.github.openxiaoshan.muse.MuseCredentialStore");
                var values = new object[args.Length + 1]; values[0] = activity; Array.Copy(args, 0, values, 1, args.Length);
                return store.CallStatic<T>(method, values);
            }
            finally { if (background) AndroidJNI.DetachCurrentThread(); }
        }
        public static MuseCredentials Load() { string value = Call<string>("load"); return string.IsNullOrEmpty(value) ? null : MuseCredentials.Parse(value); }
        public static MusePairingIdentity Identity() => new MusePairingIdentity(Call<string>("identity"));
        public static void Save(MuseCredentials value) { if (!Call<bool>("save", Json(value))) throw new MuseException("PAIR_STORAGE_FAILED"); }
        public static void Clear()
        {
            using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer"); using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
            using var store = new AndroidJavaClass("io.github.openxiaoshan.muse.MuseCredentialStore"); store.CallStatic("clear", activity);
        }
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        public static MuseCredentials Load() => MuseMacPeripheral.Load();
        public static MusePairingIdentity Identity() => MuseMacPeripheral.Identity();
        public static void Save(MuseCredentials value) => MuseMacPeripheral.Save(value);
        public static void Clear() => MuseMacPeripheral.Clear();
#else
        public static MuseCredentials Load() => null;
        public static MusePairingIdentity Identity() => MusePairingIdentity.Create();
        public static void Save(MuseCredentials value) => throw new PlatformNotSupportedException("Secure Muse storage is supported on macOS and Android.");
        public static void Clear() { }
#endif
    }
}
