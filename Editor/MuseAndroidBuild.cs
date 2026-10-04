using System.IO;
using System.Text;

// JNI names are strings; preserve the two Muse plugin classes when Android minification is enabled.
public static class MuseAndroidBuild
{
    private const string Marker = "# Muse Unity JNI";
    public static void AddKeepRules(string unityLibrary)
    {
        string path = Path.Combine(unityLibrary, "proguard-unity.txt");
        string existing = File.Exists(path) ? File.ReadAllText(path) : "";
        if (existing.Contains(Marker)) return;
        File.AppendAllText(path, "\n" + Marker + "\n-keep class io.github.openxiaoshan.muse.** { *; }\n-keep interface io.github.openxiaoshan.muse.** { *; }\n", new UTF8Encoding(false));
    }
}

#if UNITY_ANDROID
public sealed class MuseAndroidGradleProcessor : UnityEditor.Android.IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 999;
    public void OnPostGenerateGradleAndroidProject(string path)
    {
        MuseAndroidBuild.AddKeepRules(path);
        string manifest = Path.Combine(path, "src/main/AndroidManifest.xml");
        var doc = new System.Xml.XmlDocument(); doc.Load(manifest);
        const string android = "http://schemas.android.com/apk/res/android";
        foreach (string name in new[] { "INTERNET", "RECORD_AUDIO", "BLUETOOTH", "BLUETOOTH_ADMIN", "BLUETOOTH_ADVERTISE", "BLUETOOTH_CONNECT" })
        {
            string full = "android.permission." + name;
            bool present = false;
            foreach (System.Xml.XmlElement node in doc.GetElementsByTagName("uses-permission"))
                if (node.GetAttribute("name", android) == full) { present = true; break; }
            if (present) continue;
            var permission = doc.CreateElement("uses-permission"); permission.SetAttribute("name", android, full);
            if (name == "BLUETOOTH" || name == "BLUETOOTH_ADMIN") permission.SetAttribute("maxSdkVersion", android, "30");
            doc.DocumentElement.AppendChild(permission);
        }
        doc.Save(manifest);
    }
}
#endif
