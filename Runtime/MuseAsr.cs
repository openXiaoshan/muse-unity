using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Muse.Unity
{
    // Independent recorder/ASR utilities: no project-specific voice component dependency.
    public static class MuseAsr
    {
        public static byte[] EncodeWav(float[] samples, int channels, int sampleRate)
        {
            if (samples == null || samples.Length == 0 || channels < 1 || samples.Length % channels != 0 || sampleRate < 8000 || sampleRate > 48000)
                throw new MuseException("INVALID_MICROPHONE_AUDIO");
            int frames = samples.Length / channels;
            using var stream = new MemoryStream(44 + frames * 2); using var writer = new BinaryWriter(stream);
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + frames * 2);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(frames * 2);
            for (int i = 0; i < frames; i++)
            {
                double value = 0; for (int channel = 0; channel < channels; channel++) value += samples[i * channels + channel];
                value /= channels; if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
                writer.Write((short)Math.Round(Math.Max(-1, Math.Min(1, value)) * 32767));
            }
            return stream.ToArray();
        }

        public static async Task<string> TranscribeAsync(byte[] wav, string apiKey, CancellationToken cancellation, HttpMessageHandler handler = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new MuseException("ASR_KEY_REQUIRED");
            if (wav == null || wav.Length <= 44 || wav.Length > 4 * 1024 * 1024) throw new MuseException("INVALID_ASR_AUDIO");
            using var http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
            using var data = new MultipartFormDataContent();
            data.Add(new StringContent("scribe_v1"), "model_id");
            // The existing provider is retained; omitting language_code enables automatic detection.
            data.Add(new StringContent("false"), "diarize"); data.Add(new StringContent("false"), "tag_audio_events");
            var audio = new ByteArrayContent(wav); audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            data.Add(audio, "file", "muse-voice.wav");
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/speech-to-text") { Content = data };
            request.Headers.TryAddWithoutValidation("xi-api-key", apiKey);
            using var response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new MuseException("ASR_REQUEST_FAILED", (int)response.StatusCode);
            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (json.Length > 1024 * 1024) throw new MuseException("ASR_RESPONSE_TOO_LARGE");
            string text = ((string)JObject.Parse(json)["text"])?.Trim();
            if (string.IsNullOrEmpty(text)) throw new MuseException("ASR_EMPTY_TRANSCRIPT");
            if (text.Length > 12000) throw new MuseException("ASR_TRANSCRIPT_TOO_LONG");
            return text;
        }
    }
}
