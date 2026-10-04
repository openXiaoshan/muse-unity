using System;
namespace Muse.Unity
{
    // Explicit configuration only. Never inspect other components or project assets for secrets.
    public static class MuseAsrConfiguration
    {
        public static string Resolve(string supplied, string saved = null)
        {
            if (!string.IsNullOrWhiteSpace(supplied)) return supplied.Trim();
            if (!string.IsNullOrWhiteSpace(saved)) return saved.Trim();
            return Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY");
        }
    }
}
