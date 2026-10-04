# Muse Unity

**Bring your Meta Muse into a Unity experience.**

An independent, open-source Unity package with direct Muse connectivity, Bluetooth device pairing, secure credential storage, streaming text replies, and an optional voice interface. No companion bridge process is required.

[中文说明](README.zh-CN.md) · [Architecture & API](Documentation~/architecture.md) · [Development](DEVELOPMENT.md) · [Validation status](Documentation~/validation.md)

> **Public preview — `0.1.0-preview.1`.** This is a community integration, not an official Meta or Unity product. The target is Meta's personal Muse agent, not Unity's similarly named AI tooling or the Muse EEG headset.

## What is included

- C# HTTPS authentication, token refresh, Noise XX encryption and multiplexed WebSocket transport.
- Bluetooth pairing through the Muse phone app, using in-process macOS CoreBluetooth and Android BLE plugins.
- macOS Keychain and Android Keystore credential persistence.
- A standalone **Record → Send → Reply** UI. The sample transcribes audio with ElevenLabs; only the recognized text goes to Muse.
- Replaceable ASR and chat interfaces, a sample-scene generator, protocol fixtures and native-plugin source.

## Install

Use **Unity 6** (validated with 6000.3.16f1). In **Window → Package Manager → + → Install package from git URL**, enter:

```text
https://github.com/openXiaoshan/muse-unity.git#v0.1.0-preview.1
```

Or clone this repository and install its `package.json` using **Install package from disk**. Dependencies are declared in the package: Unity UI/TextMeshPro and Newtonsoft JSON. The managed Bouncy Castle dependency and its license are included.

## Try the voice sample

1. Choose **Tools → Muse Unity → Import TMP Essentials**, and let Unity finish importing.
2. Choose **Tools → Muse Unity → Create sample scene**, then **Open sample scene** and enter Play Mode.
3. Click **Bind Muse** and enter your personal [Muse SDK token](https://gadgets.muse.ai/settings/sdk-tokens). For the voice sample, enter your own ElevenLabs API key, or set `ELEVENLABS_API_KEY` before launching Unity.
4. In the Muse phone app, enable developer mode and use **Settings → Devices → Add Device**. Select the displayed `MuseGadget…` name, approve binding, and choose **Use current connection**.
5. Click **Record** once and release. Speak, then click **Send**. At the 20-second limit, the sample retains the recording until you choose Send; **Stop** discards it.

After binding, Bluetooth is no longer needed for chat. The next launch loads the saved configuration; click **Connect Muse**. The standalone package uses its own credential store and does not import another application's pairing or API keys.

The sample uses Muse's **default main conversation**, shared with the Muse app. It subscribes before sending and correlates replies to the acknowledged message. It displays the current exchange, rather than loading old conversation history. `delta.message_done` means a message completed, not that all agent work finished.

### Platform status

| Platform | Status |
| --- | --- |
| macOS Unity Editor | Native plugin builds for Apple silicon and Intel. Source implementation has real iPad pairing and Muse connection evidence. Fresh standalone-package account pairing must be tested separately. |
| Android | BLE peripheral and encrypted storage source compiles against Android SDK; physical-device and IL2CPP acceptance remain open. |
| macOS standalone | Native plugin included; host app needs Bluetooth and microphone usage descriptions. Player acceptance remains open. |
| iOS / Windows / Linux / WebGL | No supported pairing/storage integration in this preview. |

Known limitation: one Android 16 phone → macOS pairing combination failed when the phone tried to write more than the 512-byte GATT attribute limit. An iPad completed pairing with the source implementation. Do not treat that Android/macOS combination as supported.

**Live reply rendering is still under validation.** The source implementation submitted real ASR text and the Muse app showed a reply; Unity's reply subscription/correlation was then corrected. This preview distinguishes passing isolated tests from a complete live-service acceptance result. See [validation](Documentation~/validation.md).

## Use your own speech recognizer

`MuseVoiceButton.Transcribe` accepts WAV bytes and returns recognized text. Assign a delegate before the user records:

```csharp
using Muse.Unity;
using UnityEngine;

public sealed class MyMuseSpeech : MonoBehaviour
{
    [SerializeField] private MuseVoiceButton muse;

    private void Start()
    {
        muse.Transcribe = async (wav, unusedKey, cancellation) =>
        {
            // Call your ASR implementation here and return its transcript.
            return await MySpeechService.TranscribeAsync(wav, cancellation);
        };
    }
}
```

`MySpeechService` is your application code, not an included class. Subscribe to `OnTranscript`, `OnReply` and `Changed` for your own UI, or use `IMuseChatClient` / `MuseClient` directly. Keep API keys out of scenes, prefabs and source control.

## Contribute

Useful first contributions include real Android/IL2CPP tests, live reply protocol fixtures with personal data removed, device compatibility reports, and native integrations for additional platforms. Read [CONTRIBUTING.md](CONTRIBUTING.md) before sharing logs.

## License and upstream projects

Apache-2.0 for this integration, with third-party notices in [NOTICE](NOTICE). It adapts the [Meta Muse Gadget SDK](https://github.com/facebookincubator/muse-gadget-sdk) and [wong2/muse-client](https://github.com/wong2/muse-client); their exact source revisions are recorded in [DEVELOPMENT.md](DEVELOPMENT.md). Bouncy Castle has its own included license.

The code license does not grant Muse service access or change Meta's SDK-token/service terms. Bring your own authorized Muse account, SDK token and speech-provider credentials. No service credentials, branded character assets or private application code are distributed.
