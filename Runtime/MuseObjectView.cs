using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Muse.Unity
{
    // Optional UI created only by the explicitly instantiated Muse object.
    public sealed class MuseObjectView : MonoBehaviour
    {
        private MuseVoiceButton owner;
        private TMP_Text status, transcript, reply, actionLabel;
        private UnityEngine.UI.Button action, cancel, send;
        private int recordingSecond = -1;
        private GameObject setup, importSetup, conversation;
        private TMP_InputField credentials, asr, sdk, importAsr;
        private static readonly Color Ink = new Color(.10f, .11f, .15f);
        private static readonly Color Accent = new Color(.43f, .30f, .84f);
        private TMP_FontAsset font;

        public static MuseObjectView Create(MuseVoiceButton owner)
        {
            var root = new GameObject("MuseObjectView", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster));
            root.transform.SetParent(owner.transform, false);
            var view = root.AddComponent<MuseObjectView>(); view.owner = owner;
            var canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 300;
            var scaler = root.GetComponent<UnityEngine.UI.CanvasScaler>(); scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 800); scaler.matchWidthOrHeight = .5f;
            if (Object.FindFirstObjectByType<EventSystem>() == null)
            {
                var system = new GameObject("MuseEventSystem", typeof(EventSystem)); system.transform.SetParent(owner.transform, false);
#if ENABLE_INPUT_SYSTEM
                var inputModule = System.Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
                if (inputModule != null) system.AddComponent(inputModule);
                else Debug.LogError("Muse Unity: create an EventSystem with your active input module before opening the sample.");
#else
                system.AddComponent<StandaloneInputModule>();
#endif
            }
            view.Build(); owner.Changed += view.Refresh; view.Refresh(); return view;
        }
        private void OnDestroy() { if (owner != null) owner.Changed -= Refresh; }
        private RectTransform Rect(string name, Transform parent, Vector2 min, Vector2 max, Vector2 lower, Vector2 upper)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>(); rect.SetParent(parent, false);
            rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = lower; rect.offsetMax = upper; return rect;
        }
        private TMP_Text Text(string name, Transform parent, string value, float size, Vector2 min, Vector2 max, Vector2 lower, Vector2 upper)
        {
            var rect = Rect(name, parent, min, max, lower, upper); var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = font; text.text = value; text.fontSize = size; text.color = Ink;
            text.richText = false; text.raycastTarget = false; text.textWrappingMode = TextWrappingModes.Normal;
            return text;
        }
        private UnityEngine.UI.Button Button(string name, Transform parent, string label, Vector2 min, Vector2 max, Vector2 lower, Vector2 upper, bool primary, System.Action pressed)
        {
            var rect = Rect(name, parent, min, max, lower, upper);
            var image = rect.gameObject.AddComponent<UnityEngine.UI.Image>(); image.color = primary ? Accent : new Color(.94f, .93f, .98f);
            var button = rect.gameObject.AddComponent<UnityEngine.UI.Button>(); button.targetGraphic = image; button.onClick.AddListener(() => pressed());
            var text = Text("Label", rect, label, 21, Vector2.zero, Vector2.one, new Vector2(8, 0), new Vector2(-8, 0));
            text.color = primary ? Color.white : Ink; text.alignment = TextAlignmentOptions.Center;
            return button;
        }
        private void Build()
        {
            font = TMP_Settings.defaultFontAsset;
            // Assign a font with the languages your application needs.
            if (owner.GetComponent<MuseObjectFont>()?.Font != null) font = owner.GetComponent<MuseObjectFont>().Font;
            var panel = Rect("MusePanel", transform, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(-270, -335), new Vector2(270, 335));
            var bg = panel.gameObject.AddComponent<UnityEngine.UI.Image>(); bg.color = Color.white; bg.raycastTarget = false;
            Text("Title", panel, "Muse", 36, new Vector2(0, 1), Vector2.one, new Vector2(28, -68), new Vector2(-28, -18));
            Text("Subtitle", panel, "Speak to your Muse", 18, new Vector2(0, 1), Vector2.one, new Vector2(28, -97), new Vector2(-28, -69)).color = new Color(.45f, .45f, .52f);
            status = Text("Status", panel, "", 17, new Vector2(0, 1), Vector2.one, new Vector2(28, -165), new Vector2(-28, -108));
            conversation = Rect("Conversation", panel, Vector2.zero, Vector2.one, new Vector2(28, 160), new Vector2(-28, -174)).gameObject;
            var scroll = conversation.AddComponent<UnityEngine.UI.ScrollRect>(); scroll.horizontal = false;
            var viewport = Rect("Viewport", conversation.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            viewport.gameObject.AddComponent<UnityEngine.UI.Image>().color = new Color(.98f, .98f, .99f);
            viewport.gameObject.AddComponent<UnityEngine.UI.Mask>().showMaskGraphic = true;
            var content = Rect("Content", viewport, new Vector2(0, 1), Vector2.one, Vector2.zero, Vector2.zero); content.pivot = new Vector2(.5f, 1);
            var layout = content.gameObject.AddComponent<UnityEngine.UI.VerticalLayoutGroup>(); layout.padding = new RectOffset(14, 14, 14, 14);
            layout.spacing = 12; layout.childControlHeight = true; layout.childControlWidth = true; layout.childForceExpandHeight = false;
            content.gameObject.AddComponent<UnityEngine.UI.ContentSizeFitter>().verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
            transcript = Text("Transcript", content, "Your recognized speech will appear here.", 20, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            transcript.gameObject.AddComponent<UnityEngine.UI.LayoutElement>().minHeight = 56;
            reply = Text("Reply", content, "Muse's reply will appear here.", 22, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            reply.gameObject.AddComponent<UnityEngine.UI.LayoutElement>().minHeight = 90;
            scroll.viewport = viewport; scroll.content = content;
            action = Button("SpeakButton", panel, "Connect Muse", Vector2.zero, new Vector2(.5f, 0), new Vector2(28, 85), new Vector2(-6, 145), true, () => owner.Press());
            actionLabel = action.GetComponentInChildren<TMP_Text>();
            send = Button("SendButton", panel, "Send", new Vector2(.5f, 0), new Vector2(1, 0), new Vector2(6, 85), new Vector2(-28, 145), true, () => owner.StopRecordingAndSend());
            cancel = Button("CancelButton", panel, "Stop", Vector2.zero, new Vector2(1f / 3, 0), new Vector2(28, 24), new Vector2(-6, 72), false, () => owner.Cancel());
            Button("BindMuseButton", panel, "Bind Muse", new Vector2(1f / 3, 0), new Vector2(2f / 3, 0), new Vector2(6, 24), new Vector2(-6, 72), false, ShowSetup);
            Button("SetupButton", panel, "Setup", new Vector2(2f / 3, 0), new Vector2(1, 0), new Vector2(6, 24), new Vector2(-28, 72), false, ShowSetup);

            setup = Rect("Setup", panel, Vector2.zero, Vector2.one, new Vector2(20, 16), new Vector2(-20, -171)).gameObject;
            var setupBg = setup.AddComponent<UnityEngine.UI.Image>(); setupBg.color = Color.white;
            Text("SdkLabel", setup.transform, "Personal Muse SDK token", 18, new Vector2(0, 1), Vector2.one, new Vector2(8, -28), new Vector2(-8, -2));
            sdk = Input("SdkToken", setup.transform, "From gadgets.muse.ai/settings/sdk-tokens", 48, -34, false);
            Text("AsrLabel", setup.transform, "ElevenLabs ASR key", 18, new Vector2(0, 1), Vector2.one, new Vector2(8, -116), new Vector2(-8, -90));
            asr = Input("AsrKey", setup.transform, "Optional override; uses saved key or environment", 48, -122, false);
            Text("PairingSteps", setup.transform,
                "1. Connect this device to the internet and turn on Bluetooth.\n2. Tap Start binding; allow Nearby devices.\n3. In the Muse phone app: Settings > Devices > Developer mode > Add Device.\n4. Select the displayed MuseGadget name, confirm, then choose Use current connection.",
                16, new Vector2(0, 1), Vector2.one, new Vector2(8, -344), new Vector2(-8, -181));
            Button("StartBindingButton", setup.transform, "Start binding", Vector2.zero, new Vector2(.5f, 0), new Vector2(8, 77), new Vector2(-6, 131), true, StartBinding);
            Button("ImportCredentialsButton", setup.transform, "Import JSON", new Vector2(.5f, 0), new Vector2(1, 0), new Vector2(6, 77), new Vector2(-8, 131), false,
                () => { setup.SetActive(false); importAsr.text = asr.text; asr.text = ""; sdk.text = ""; importSetup.SetActive(true); });
            Button("DiscoveryButton", setup.transform, "Scan test", Vector2.zero, new Vector2(.5f, 0), new Vector2(8, 12), new Vector2(-6, 66), false,
                () => { sdk.text = ""; asr.text = ""; setup.SetActive(false); owner.BeginBluetoothDiscovery(); });
            Button("Back", setup.transform, "Back", new Vector2(.5f, 0), new Vector2(1, 0), new Vector2(6, 12), new Vector2(-8, 66), false, () => setup.SetActive(false));
            importSetup = Rect("ImportSetup", panel, Vector2.zero, Vector2.one, new Vector2(20, 16), new Vector2(-20, -171)).gameObject;
            importSetup.AddComponent<UnityEngine.UI.Image>().color = Color.white;
            Text("CredentialsLabel", importSetup.transform, "Muse paired-device credentials (JSON)", 18, new Vector2(0, 1), Vector2.one, new Vector2(8, -28), new Vector2(-8, -2));
            credentials = Input("Credentials", importSetup.transform, "Paste paired credentials JSON", 190, -36, true);
            Text("ImportAsrLabel", importSetup.transform, "ElevenLabs ASR key", 18, new Vector2(0, 1), Vector2.one, new Vector2(8, -265), new Vector2(-8, -239));
            importAsr = Input("ImportAsrKey", importSetup.transform, "Optional override; uses saved key or environment", 50, -274, false);
            Text("StorageNote", importSetup.transform, "Binding and ASR key are saved securely on Mac and Android and restored next time.", 16, Vector2.zero, new Vector2(1, 0), new Vector2(8, 82), new Vector2(-8, 134));
            Button("Apply", importSetup.transform, "Apply", Vector2.zero, new Vector2(.5f, 0), new Vector2(8, 12), new Vector2(-6, 66), true, Apply);
            Button("ImportBack", importSetup.transform, "Back", new Vector2(.5f, 0), new Vector2(1, 0), new Vector2(6, 12), new Vector2(-8, 66), false, () => importSetup.SetActive(false));
            importSetup.SetActive(false);
            setup.SetActive(false);
        }
        private TMP_InputField Input(string name, Transform parent, string hint, float height, float top, bool multiline)
        {
            var rect = Rect(name, parent, new Vector2(0, 1), Vector2.one, new Vector2(8, top - height), new Vector2(-8, top));
            rect.gameObject.AddComponent<UnityEngine.UI.Image>().color = new Color(.95f, .95f, .98f);
            var input = rect.gameObject.AddComponent<TMP_InputField>();
            var area = Rect("TextArea", rect, Vector2.zero, Vector2.one, new Vector2(12, 9), new Vector2(-12, -9)); area.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();
            var label = Text("Text", area, "", 16, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var placeholder = Text("Placeholder", area, hint, 16, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero); placeholder.color = new Color(.5f, .5f, .56f);
            input.textViewport = area; input.textComponent = (TextMeshProUGUI)label; input.placeholder = placeholder;
            input.contentType = TMP_InputField.ContentType.Password; input.characterLimit = multiline ? 16384 : 512;
            if (multiline) input.lineType = TMP_InputField.LineType.MultiLineNewline;
            return input;
        }
        private void ShowSetup() { if (owner.IsBusy) owner.Cancel(); importSetup.SetActive(false); setup.SetActive(true); }
        private void StartBinding()
        {
            owner.BeginBluetoothBinding(string.IsNullOrWhiteSpace(sdk.text) ? System.Environment.GetEnvironmentVariable("MUSE_SDK_TOKEN") : sdk.text, asr.text);
            sdk.text = ""; asr.text = ""; setup.SetActive(false);
        }
        private void Apply()
        {
            try
            {
                owner.Configure(MuseCredentials.Parse(credentials.text), importAsr.text, persist: true);
                credentials.text = ""; importAsr.text = ""; importSetup.SetActive(false);
            }
            catch { status.text = "Could not apply/save configuration. Check paired JSON and secure storage."; }
        }
        private void Refresh()
        {
            if (owner == null || status == null) return;
            status.text = owner.Status;
            transcript.text = string.IsNullOrEmpty(owner.Transcript) ? "Your recognized speech will appear here." : "You\n" + owner.Transcript;
            reply.text = string.IsNullOrEmpty(owner.Reply) ? "Muse's reply will appear here." : "Muse\n" + owner.Reply;
            actionLabel.text = owner.IsPairing ? "Binding Muse…" : owner.IsOpeningMicrophone ? "Opening microphone…" : owner.CanSendRecording ? "Recorded " + Mathf.FloorToInt(owner.RecordedSeconds) + "s" : owner.IsConnected ? "Record" : "Connect Muse";
            action.interactable = !owner.IsBusy && !owner.CanSendRecording;
            send.interactable = owner.CanSendRecording;
            cancel.interactable = owner.IsBusy || owner.CanSendRecording || owner.State == MuseVoiceButton.VoiceState.ReplyReceived;
        }
        private void Update()
        {
            if (owner == null) return;
            int seconds = Mathf.FloorToInt(owner.RecordedSeconds);
            if (seconds != recordingSecond) { recordingSecond = seconds; Refresh(); }
        }
    }

}
