package io.github.openxiaoshan.muse;

import android.app.Activity;
import android.bluetooth.*;
import android.bluetooth.le.*;
import android.content.Context;
import android.content.pm.PackageManager;
import android.os.Handler;
import android.os.Looper;
import android.os.ParcelUuid;
import android.os.Build;
import android.util.Base64;
import java.util.ArrayDeque;
import java.util.Arrays;
import java.util.UUID;

/** In-process BLE peripheral only. Pairing crypto and credentials remain in Unity C#. */
public final class MuseBlePeripheral {
    public interface Listener { void onEvent(String kind, String value); }
    public static final UUID SERVICE = UUID.fromString("7fdd3d1c-38ea-46cf-8b46-314ecf5f240c");
    public static final UUID RX = UUID.fromString("4d593029-28a2-4a6e-a1f0-3c2d5e8f9b01");
    public static final UUID TX = UUID.fromString("d75dc4ca-7b2b-4e9c-8f0a-1d2e3f4a5b6c");
    private static final UUID CCCD = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb");
    private final Context context;
    private final Listener listener;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ArrayDeque<byte[]> queue = new ArrayDeque<>();
    private BluetoothAdapter adapter;
    private BluetoothGattServer server;
    private BluetoothLeAdvertiser advertiser;
    private BluetoothGattCharacteristic tx;
    private BluetoothDevice phone;
    private String oldName, name;
    private boolean closed, started, subscribed, sending, completed, finishing;
    private byte[] last = new byte[0];
    public MuseBlePeripheral(Activity activity, Listener listener) { this.context = activity; this.listener = listener; }
    private void emit(String kind, String value) { if (!closed) listener.onEvent(kind, value); }
    private void fail(String code) { emit("error", code); stopNow(); }
    public void start(String requestedName) {
        main.post(() -> {
            if (closed || started) return;
            started = true;
            if (!requestedName.matches("MuseGadget[0-9A-F]{6}")) { fail("PAIR_NAME_INVALID"); return; }
            name = requestedName;
            try {
                if (!context.getPackageManager().hasSystemFeature(PackageManager.FEATURE_BLUETOOTH_LE)) { fail("BLE_NOT_SUPPORTED"); return; }
                BluetoothManager manager = (BluetoothManager)context.getSystemService(Context.BLUETOOTH_SERVICE);
                adapter = manager == null ? null : manager.getAdapter();
                if (adapter == null || !adapter.isEnabled()) { fail("BLUETOOTH_OFF"); return; }
                if (!adapter.isMultipleAdvertisementSupported()) { fail("BLE_PERIPHERAL_NOT_SUPPORTED"); return; }
                advertiser = adapter.getBluetoothLeAdvertiser();
                if (advertiser == null) { fail("BLE_PERIPHERAL_NOT_SUPPORTED"); return; }
                oldName = adapter.getName();
                if (!adapter.setName(name)) { fail("BLE_NAME_FAILED"); return; }
                server = manager.openGattServer(context, callbacks);
                if (server == null) { fail("BLE_GATT_FAILED"); return; }
                BluetoothGattService service = new BluetoothGattService(SERVICE, BluetoothGattService.SERVICE_TYPE_PRIMARY);
                service.addCharacteristic(new BluetoothGattCharacteristic(RX,
                    BluetoothGattCharacteristic.PROPERTY_WRITE | BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE,
                    BluetoothGattCharacteristic.PERMISSION_WRITE));
                tx = new BluetoothGattCharacteristic(TX, BluetoothGattCharacteristic.PROPERTY_READ | BluetoothGattCharacteristic.PROPERTY_NOTIFY,
                    BluetoothGattCharacteristic.PERMISSION_READ);
                tx.addDescriptor(new BluetoothGattDescriptor(CCCD, BluetoothGattDescriptor.PERMISSION_READ | BluetoothGattDescriptor.PERMISSION_WRITE));
                service.addCharacteristic(tx);
                if (!server.addService(service)) { fail("BLE_GATT_FAILED"); return; }
                main.postDelayed(() -> { if (!closed && !completed) fail("PAIR_WINDOW_EXPIRED"); }, 600000);
            } catch (SecurityException e) { fail("BLUETOOTH_PERMISSION_REQUIRED"); }
              catch (Exception e) { fail("BLE_START_FAILED"); }
        });
    }
    private final AdvertiseCallback advertising = new AdvertiseCallback() {
        @Override public void onStartSuccess(AdvertiseSettings settings) { main.post(() -> emit("advertising", name)); }
        @Override public void onStartFailure(int code) { main.post(() -> fail("BLE_ADVERTISE_FAILED_" + code)); }
    };
    private boolean same(BluetoothDevice device) { return phone != null && phone.equals(device); }
    private final BluetoothGattServerCallback callbacks = new BluetoothGattServerCallback() {
        @Override public void onServiceAdded(int status, BluetoothGattService service) { main.post(() -> {
            if (closed) return;
            if (status != BluetoothGatt.GATT_SUCCESS) { fail("BLE_GATT_FAILED"); return; }
            // Name in primary advertising; service UUID in scan response keeps both under 31 bytes.
            main.postDelayed(() -> advertise(0), 250);
        }); }
        @Override public void onConnectionStateChange(BluetoothDevice device, int status, int state) { main.post(() -> {
            if (closed) return;
            try {
                if (state == BluetoothProfile.STATE_CONNECTED) {
                    if (phone != null && !same(device)) { server.cancelConnection(device); return; }
                    phone = device; emit("connected", "");
                } else if (state == BluetoothProfile.STATE_DISCONNECTED && same(device)) {
                    if (completed) { emit("completed", ""); stopNow(); } else fail("PAIR_PHONE_DISCONNECTED");
                }
            } catch (Exception e) { fail("BLE_CONNECTION_FAILED"); }
        }); }
        @Override public void onCharacteristicWriteRequest(BluetoothDevice device, int request, BluetoothGattCharacteristic characteristic,
                boolean prepared, boolean respond, int offset, byte[] value) {
            final byte[] copy = value == null ? null : value.clone();
            main.post(() -> {
                if (closed) return;
                boolean valid = !completed && same(device) && RX.equals(characteristic.getUuid()) && !prepared && offset == 0 && copy != null && copy.length > 0 && copy.length <= 8192;
                try {
                    if (respond) server.sendResponse(device, request, valid ? BluetoothGatt.GATT_SUCCESS : BluetoothGatt.GATT_REQUEST_NOT_SUPPORTED, 0, null);
                    if (valid) emit("write", Base64.encodeToString(copy, Base64.NO_WRAP));
                } catch (Exception e) { fail("BLE_WRITE_FAILED"); }
                finally { if (copy != null) Arrays.fill(copy, (byte)0); }
            });
        }
        @Override public void onCharacteristicReadRequest(BluetoothDevice device, int request, int offset, BluetoothGattCharacteristic characteristic) { main.post(() -> {
            if (closed) return;
            boolean valid = same(device) && TX.equals(characteristic.getUuid()) && offset >= 0 && offset <= last.length;
            try { server.sendResponse(device, request, valid ? BluetoothGatt.GATT_SUCCESS : BluetoothGatt.GATT_INVALID_OFFSET, offset,
                    valid ? Arrays.copyOfRange(last, offset, last.length) : null); }
            catch (Exception e) { fail("BLE_READ_FAILED"); }
        }); }
        @Override public void onDescriptorReadRequest(BluetoothDevice device, int request, int offset, BluetoothGattDescriptor descriptor) { main.post(() -> {
            if (closed) return;
            try { server.sendResponse(device, request, same(device) && CCCD.equals(descriptor.getUuid()) && offset == 0 ? BluetoothGatt.GATT_SUCCESS : BluetoothGatt.GATT_REQUEST_NOT_SUPPORTED,
                0, subscribed ? BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE : BluetoothGattDescriptor.DISABLE_NOTIFICATION_VALUE); }
            catch (Exception e) { fail("BLE_READ_FAILED"); }
        }); }
        @Override public void onDescriptorWriteRequest(BluetoothDevice device, int request, BluetoothGattDescriptor descriptor,
                boolean prepared, boolean respond, int offset, byte[] value) {
            final byte[] copy = value == null ? null : value.clone();
            main.post(() -> {
                if (closed) return;
                boolean enable = Arrays.equals(copy, BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE);
                boolean disable = Arrays.equals(copy, BluetoothGattDescriptor.DISABLE_NOTIFICATION_VALUE);
                boolean valid = same(device) && CCCD.equals(descriptor.getUuid()) && !prepared && offset == 0 && (enable || disable);
                try {
                    if (respond) server.sendResponse(device, request, valid ? BluetoothGatt.GATT_SUCCESS : BluetoothGatt.GATT_REQUEST_NOT_SUPPORTED, 0, null);
                    if (valid) { subscribed = enable; if (!enable && !completed) fail("PAIR_PHONE_DISCONNECTED"); else flush(); }
                } catch (Exception e) { fail("BLE_SUBSCRIBE_FAILED"); }
            });
        }
        @Override public void onExecuteWrite(BluetoothDevice device, int request, boolean execute) { main.post(() -> {
            if (closed) return;
            try { server.sendResponse(device, request, BluetoothGatt.GATT_REQUEST_NOT_SUPPORTED, 0, null); }
            catch (Exception e) { fail("BLE_WRITE_FAILED"); }
        }); }
        @Override public void onNotificationSent(BluetoothDevice device, int status) { main.post(() -> {
            if (closed || !same(device)) return;
            if (status != BluetoothGatt.GATT_SUCCESS) { fail("BLE_NOTIFY_FAILED"); return; }
            sending = false; main.postDelayed(() -> flush(), 50);
        }); }
    };
    private void advertise(int retry) {
        if (closed) return;
        try {
            if (!name.equals(adapter.getName())) {
                if (retry >= 20) { fail("BLE_NAME_FAILED"); return; }
                main.postDelayed(() -> advertise(retry + 1), 150); return;
            }
            advertiser.startAdvertising(new AdvertiseSettings.Builder().setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_LATENCY)
                .setConnectable(true).setTimeout(0).build(), new AdvertiseData.Builder().setIncludeDeviceName(true).build(),
                new AdvertiseData.Builder().addServiceUuid(new ParcelUuid(SERVICE)).build(), advertising);
        } catch (SecurityException e) { fail("BLUETOOTH_PERMISSION_REQUIRED"); }
          catch (Exception e) { fail("BLE_ADVERTISE_FAILED"); }
    }
    public void send(String encoded) { main.post(() -> {
        if (closed || completed) return;
        try {
            byte[] value = Base64.decode(encoded, Base64.NO_WRAP);
            if (value.length > 20 || queue.size() >= 2048) { fail("BLE_NOTIFY_QUEUE_FAILED"); return; }
            queue.add(value); flush();
        } catch (Exception e) { fail("BLE_NOTIFY_FAILED"); }
    }); }
    @SuppressWarnings("deprecation") private void flush() {
        if (closed || sending) return;
        if (queue.isEmpty()) {
            if (completed && !finishing) { finishing = true; main.postDelayed(() -> { if (!closed) { emit("completed", ""); stopNow(); } }, 1500); }
            return;
        }
        if (!subscribed || phone == null) return;
        try {
            last = queue.remove(); sending = true;
            if (Build.VERSION.SDK_INT >= 33) {
                if (server.notifyCharacteristicChanged(phone, tx, false, last) != BluetoothStatusCodes.SUCCESS) fail("BLE_NOTIFY_FAILED");
            } else { tx.setValue(last); if (!server.notifyCharacteristicChanged(phone, tx, false)) fail("BLE_NOTIFY_FAILED"); }
        } catch (Exception e) { fail("BLE_NOTIFY_FAILED"); }
    }
    public void complete() { main.post(() -> { if (!closed) { completed = true; flush(); } }); }
    public void stop() { main.post(() -> stopNow()); }
    private void stopNow() {
        if (closed) return; closed = true; main.removeCallbacksAndMessages(null);
        try { if (advertiser != null) advertiser.stopAdvertising(advertising); } catch (Exception ignored) { }
        try { if (server != null) { server.clearServices(); server.close(); } } catch (Exception ignored) { }
        try { if (adapter != null && oldName != null && name.equals(adapter.getName())) adapter.setName(oldName); } catch (Exception ignored) { }
        for (byte[] value : queue) Arrays.fill(value, (byte)0); queue.clear(); Arrays.fill(last, (byte)0);
        phone = null; server = null;
    }
}
