package io.github.openxiaoshan.muse;

import android.content.Context;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.AtomicFile;
import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.security.SecureRandom;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/** Muse-only encrypted storage, scoped to this application. */
public final class MuseCredentialStore {
    private static final String ALIAS = "muse.unity.device.v1";
    private static AtomicFile file(Context context) { return new AtomicFile(new File(context.getNoBackupFilesDir(), "muse-device.v1")); }
    private static SecretKey key() throws Exception {
        KeyStore store = KeyStore.getInstance("AndroidKeyStore"); store.load(null);
        if (!store.containsAlias(ALIAS)) {
            KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
            generator.init(new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setRandomizedEncryptionRequired(true).build()); generator.generateKey();
        }
        return (SecretKey)store.getKey(ALIAS, null);
    }
    public static synchronized String identity(Context context) {
        String value = context.getSharedPreferences("muse-identity", Context.MODE_PRIVATE).getString("mac", "");
        if (value.matches("[0-9a-f]{2}(:[0-9a-f]{2}){5}")) return value;
        byte[] bytes = new byte[6]; new SecureRandom().nextBytes(bytes); bytes[0] = (byte)((bytes[0] & 0xfc) | 2);
        StringBuilder result = new StringBuilder();
        for (int i = 0; i < bytes.length; i++) { if (i != 0) result.append(':'); result.append(String.format(java.util.Locale.ROOT, "%02x", bytes[i] & 255)); }
        value = result.toString();
        if (!context.getSharedPreferences("muse-identity", Context.MODE_PRIVATE).edit().putString("mac", value).commit()) throw new IllegalStateException("Identity storage failed");
        return value;
    }
    public static synchronized String load(Context context) throws Exception {
        AtomicFile target = file(context); if (!target.getBaseFile().exists()) return "";
        byte[] bytes = target.readFully(); if (bytes.length < 29 || bytes[0] != 1) throw new IllegalStateException("Invalid credential format");
        byte[] iv = new byte[12]; System.arraycopy(bytes, 1, iv, 0, 12);
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding"); cipher.init(Cipher.DECRYPT_MODE, key(), new GCMParameterSpec(128, iv));
        return new String(cipher.doFinal(bytes, 13, bytes.length - 13), StandardCharsets.UTF_8);
    }
    public static synchronized boolean save(Context context, String json) throws Exception {
        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding"); cipher.init(Cipher.ENCRYPT_MODE, key());
        byte[] encrypted = cipher.doFinal(json.getBytes(StandardCharsets.UTF_8));
        AtomicFile target = file(context); FileOutputStream stream = null;
        try { stream = target.startWrite(); stream.write(1); stream.write(cipher.getIV()); stream.write(encrypted); target.finishWrite(stream); return true; }
        catch (Exception e) { if (stream != null) target.failWrite(stream); throw e; }
    }
    public static synchronized void clear(Context context) throws Exception {
        file(context).delete(); KeyStore store = KeyStore.getInstance("AndroidKeyStore"); store.load(null);
        if (store.containsAlias(ALIAS)) store.deleteEntry(ALIAS);
    }
}
