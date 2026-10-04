using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace Muse.Unity
{
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
    public sealed class MuseMacPeripheral : IMuseBlePeripheral
    {
        private const string Library = "MuseBleNative";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeEvent(int id, string kind, string value);
        private static readonly NativeEvent Callback = OnEvent;
        private static readonly ConcurrentDictionary<int, Action<string, string>> listeners = new ConcurrentDictionary<int, Action<string, string>>();
        private static int sequence;
        private IntPtr handle;
        private int identity;
        [DllImport(Library)] private static extern IntPtr muse_ble_create(int id, NativeEvent callback);
        [DllImport(Library)] private static extern void muse_ble_start(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Library)] private static extern void muse_ble_send(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string packet);
        [DllImport(Library)] private static extern void muse_ble_complete(IntPtr handle);
        [DllImport(Library)] private static extern void muse_ble_dispose(IntPtr handle);
        [DllImport(Library)] private static extern int muse_store_load(out IntPtr value);
        [DllImport(Library)] private static extern int muse_store_save([MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport(Library)] private static extern int muse_store_clear();
        [DllImport(Library)] private static extern IntPtr muse_store_identity();
        [DllImport(Library)] private static extern void muse_free(IntPtr value);
        [AOT.MonoPInvokeCallback(typeof(NativeEvent))]
        private static void OnEvent(int id, string kind, string value) { if (listeners.TryGetValue(id, out var callback)) callback(kind, value); }
        public void Start(string name, Action<string, string> onEvent)
        {
            identity = Interlocked.Increment(ref sequence); listeners[identity] = onEvent;
            try { handle = muse_ble_create(identity, Callback); if (handle == IntPtr.Zero) throw new MuseException("BLE_START_FAILED"); muse_ble_start(handle, name); }
            catch { listeners.TryRemove(identity, out _); throw; }
        }
        public void Send(byte[] packet) => muse_ble_send(handle, Convert.ToBase64String(packet));
        public void Complete() => muse_ble_complete(handle);
        public void Dispose() { listeners.TryRemove(identity, out _); if (handle != IntPtr.Zero) { muse_ble_dispose(handle); handle = IntPtr.Zero; } }
        public static MuseCredentials Load()
        {
            IntPtr value = IntPtr.Zero;
            try { if (muse_store_load(out value) != 0) throw new MuseException("PAIR_STORAGE_FAILED"); return value == IntPtr.Zero ? null : MuseCredentials.Parse(Marshal.PtrToStringAnsi(value)); }
            catch (DllNotFoundException) { return null; } // Isolated source tests omit the native plugin.
            finally { if (value != IntPtr.Zero) muse_free(value); }
        }
        public static MusePairingIdentity Identity()
        {
            IntPtr value = muse_store_identity(); if (value == IntPtr.Zero) throw new MuseException("PAIR_STORAGE_FAILED");
            try { return new MusePairingIdentity(Marshal.PtrToStringAnsi(value)); } finally { muse_free(value); }
        }
        public static void Save(MuseCredentials credentials) { if (muse_store_save(MuseDeviceStore.Json(credentials)) != 0) throw new MuseException("PAIR_STORAGE_FAILED"); }
        public static void Clear() { if (muse_store_clear() != 0) throw new MuseException("PAIR_STORAGE_FAILED"); }
    }
#endif
}
