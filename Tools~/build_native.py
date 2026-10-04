#!/usr/bin/env python3
"""Rebuild native plugins from source. This never reads credentials or starts BLE."""
import argparse,os,plistlib,subprocess,tempfile
from pathlib import Path
root=Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser();p.add_argument('--mac',action='store_true');p.add_argument('--android-sdk',type=Path);p.add_argument('--jdk',type=Path);a=p.parse_args()
if a.mac:
    bundle=root/'Runtime/Plugins/macOS/MuseBleNative.bundle';binary=bundle/'Contents/MacOS/MuseBleNative';binary.parent.mkdir(parents=True,exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='muse-unity-clang-') as cache:
        subprocess.run(['xcrun','clang','-fobjc-arc','-fmodules','-fmodules-cache-path='+cache,'-dynamiclib','-arch','arm64','-arch','x86_64','-mmacosx-version-min=11.0','-framework','Foundation','-framework','CoreBluetooth','-framework','Security','-framework','LocalAuthentication',str(root/'Native~/macOS/MuseBleNative.m'),'-o',str(binary)],check=True)
    (bundle/'Contents/Info.plist').write_bytes(plistlib.dumps({'CFBundleExecutable':'MuseBleNative','CFBundleIdentifier':'io.github.openxiaoshan.muse.Native','CFBundlePackageType':'BNDL','CFBundleShortVersionString':'0.1.0','CFBundleVersion':'1'}))
    print(subprocess.check_output(['lipo','-archs',str(binary)],text=True).strip())
    symbols=subprocess.check_output(['nm','-arch','arm64','-gU',str(binary)],text=True)
    for name in ['muse_ble_create','muse_ble_start','muse_ble_send','muse_ble_complete','muse_ble_dispose','muse_store_load','muse_store_save','muse_store_clear','muse_store_identity','muse_free']:
        assert '_'+name in symbols,name
    print('PASS macOS universal plugin and C ABI')
if a.android_sdk:
    sdk=a.android_sdk;platforms=sorted((sdk/'platforms').glob('android-*/android.jar'),key=lambda x:int(x.parent.name.split('-')[-1]))
    if not platforms: raise SystemExit('Android SDK platform missing')
    javac=str(a.jdk/'bin/javac') if a.jdk else 'javac';javap=str(a.jdk/'bin/javap') if a.jdk else 'javap'
    with tempfile.TemporaryDirectory(prefix='muse-unity-java-') as out:
        subprocess.run([javac,'--release','8','-cp',str(platforms[-1]),'-d',out,*map(str,(root/'Runtime/Plugins/Android').glob('*.java'))],check=True)
        signatures=subprocess.check_output([javap,'-s','-classpath',out,'io.github.openxiaoshan.muse.MuseBlePeripheral','io.github.openxiaoshan.muse.MuseBlePeripheral$Listener','io.github.openxiaoshan.muse.MuseCredentialStore'],text=True)
        for token in ['void start(java.lang.String)','void send(java.lang.String)','void complete()','void stop()','void onEvent(java.lang.String, java.lang.String)','java.lang.String load(android.content.Context)','boolean save(android.content.Context, java.lang.String)']: assert token in signatures,token
    print('PASS Android source compilation and JNI contracts')
if not a.mac and not a.android_sdk: p.error('select --mac and/or --android-sdk')
