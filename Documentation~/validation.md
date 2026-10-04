# Validation status

Publication date: 2026-10-04. Initial public preview, published before standalone acceptance is complete.

## Passed for the extracted package

- macOS native source rebuilt as an arm64/x86_64 universal plugin; expected C ABI symbols verified.
- Android Java source compiled against an Android SDK; expected JNI methods verified.
- Selected-file extraction, explicit credential configuration, separate namespace/store identifiers, and pre-publication credential/private-dependency scan.

## Pending for the extracted package

- Fresh-project UPM installation succeeded. The first compile exposed a managed crypto plugin import issue; its importer metadata was corrected, and compilation plus isolated C#/protocol/UI tests await a rerun.
- New account binding and credential restoration using the package's independent store.
- Complete live speech → Muse → visible Unity reply acceptance.
- Android device / IL2CPP / APK, macOS standalone, and multilingual font coverage.

## Source-implementation evidence (not standalone acceptance)

The original implementation passed isolated C# protocol/UI fixtures and had real iPad → Mac BLE authorization, encrypted credential saving, Muse connection and ElevenLabs transcription. A user saw the submitted message and reply in the Muse app. Unity reply handling was subsequently changed to use the existing main conversation, pre-subscribe and correlate canonical message IDs. Complete live Unity reply rendering has not yet been confirmed.

One Android 16 → macOS pairing attempt hit a phone-side GATT attribute size limit. That combination remains unsupported until revalidated. No physical-device claims are made from compilation or synthetic fixtures.
