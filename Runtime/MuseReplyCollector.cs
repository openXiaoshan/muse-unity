using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Muse.Unity
{
    // Filter live events to the current acknowledged request; unrelated App activity is excluded.
    public sealed class MuseReplyCollector
    {
        private readonly string sessionId, userMessageId, parentMessageId;
        private readonly bool scopedSubscription;
        private readonly HashSet<string> relatedMessages = new HashSet<string>();
        private readonly Dictionary<string, string> messages = new Dictionary<string, string>();
        private readonly List<string> order = new List<string>();
        public string Text { get; private set; } = "";
        public bool MessageReady { get; private set; }
        // Fixed reason codes only: diagnostics never contain text, IDs or account details.
        public string LastDecision { get; private set; } = "none";
        private bool Reject(string reason) { LastDecision = reason; return false; }
        public MuseReplyCollector(string sessionId, string userMessageId, string parentMessageId = null, bool scopedSubscription = true)
        { this.sessionId = sessionId; this.userMessageId = userMessageId; this.parentMessageId = parentMessageId; this.scopedSubscription = scopedSubscription; }
        public bool Receive(JObject value)
        {
            var p = value["payload"] as JObject; if (p == null) return Reject("no_payload");
            string session = (string)p["session_id"] ?? (string)value["session_id"];
            if (!string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(session) && session != sessionId) return Reject("other_session");
            string replyTo = (string)p["reply_to_message_id"] ?? (string)p["parent_message_id"] ?? (string)value["reply_to_message_id"] ?? (string)value["parent_message_id"];
            if (!string.IsNullOrEmpty(replyTo) && replyTo != userMessageId && replyTo != parentMessageId) return Reject("other_parent");
            string id = (string)p["message_id"] ?? (string)value["message_id"] ?? (string)p["id"];
            if (string.IsNullOrEmpty(id)) return Reject("no_message_id");
            if (id == userMessageId || id == parentMessageId) return Reject("user_message");
            string kind = (string)value["event"] ?? (string)value["event_name"];
            bool related = !string.IsNullOrEmpty(replyTo) && (replyTo == userMessageId || replyTo == parentMessageId);
            if (!scopedSubscription && !related && !relatedMessages.Contains(id)) return Reject("unrelated_message");
            if (related) relatedMessages.Add(id);
            if (relatedMessages.Count > 64) throw new MuseException("MUSE_REPLY_TOO_LARGE");
            if (kind == "delta.message_start") return Reject("related_start");
            messages.TryGetValue(id, out var text); text ??= "";
            if (kind == "delta.text_append" && p["text"]?.Type == JTokenType.String) text += (string)p["text"];
            else if (kind == "delta.message_done" || kind == "message.assistant")
            {
                if (kind == "message.assistant" && (bool?)p["display_text_ready"] == false) return Reject("text_not_ready");
                if (p["display_text"]?.Type == JTokenType.String) text = (string)p["display_text"];
                else if (p["content"]?.Type == JTokenType.String) text = (string)p["content"];
                else if (p["text"]?.Type == JTokenType.String) text = (string)p["text"];
                if (!string.IsNullOrWhiteSpace(text)) MessageReady = true;
            }
            else return Reject("other_event");
            if (!messages.ContainsKey(id)) order.Add(id); messages[id] = text;
            Text = string.Join("\n\n", order.Select(key => messages[key]).Where(v => !string.IsNullOrEmpty(v)));
            if (messages.Count > 64 || Text.Length > 64000) throw new MuseException("MUSE_REPLY_TOO_LARGE");
            LastDecision = MessageReady ? "accepted_final" : "accepted_partial"; return true;
        }
    }
}
