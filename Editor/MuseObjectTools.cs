using System;
using System.IO;
using Muse.Unity;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class MuseObjectTools
{
    public const string PrefabPath = "Assets/MuseUnitySample/MuseObject.prefab";
    public const string ScenePath = "Assets/MuseUnitySample/MuseObjectTest.unity";
    [MenuItem("Tools/Muse Unity/Import TMP Essentials")]
    public static void ImportEssentials() => TMP_PackageResourceImporter.ImportResources(true, false, false);
    [MenuItem("Tools/Muse Unity/Create sample scene")]
    public static void CreateAssets()
    {
        if (Application.isPlaying) throw new InvalidOperationException("Exit Play Mode before creating the sample.");
        if (TMP_Settings.defaultFontAsset == null) throw new InvalidOperationException("First use Tools > Muse Unity > Import TMP Essentials, then create the sample.");
        Directory.CreateDirectory("Assets/MuseUnitySample"); AssetDatabase.Refresh();
        var preview = EditorSceneManager.NewPreviewScene();
        var root = new GameObject("MuseObject"); SceneManager.MoveGameObjectToScene(root, preview);
        try {
            root.AddComponent<MuseObjectFont>().Font = TMP_Settings.defaultFontAsset;
            root.AddComponent<MuseVoiceButton>(); PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        } finally { UnityEngine.Object.DestroyImmediate(root); EditorSceneManager.ClosePreviewScene(preview); }
        var previous = SceneManager.GetActiveScene();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        try {
            SceneManager.SetActiveScene(scene);
            PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath), scene);
            var camera = new GameObject("MusePreviewCamera", typeof(Camera)).GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.93f, .92f, .97f);
            EditorSceneManager.SaveScene(scene, ScenePath);
        } finally { EditorSceneManager.CloseScene(scene, true); if (previous.IsValid()) SceneManager.SetActiveScene(previous); }
        AssetDatabase.SaveAssets();
        Debug.Log("Muse Unity sample created. Open Assets/MuseUnitySample/MuseObjectTest.unity and enter Play Mode.");
    }
    [MenuItem("Tools/Muse Unity/Open sample scene")]
    public static void OpenTest()
    {
        if (Application.isPlaying) throw new InvalidOperationException("Exit Play Mode first.");
        if (!File.Exists(ScenePath)) CreateAssets();
        if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(ScenePath);
    }
    // CI preparation imports Unity-owned resources into the disposable host project, never this package.
    public static void PrepareTests()
    {
        if (TMP_Settings.defaultFontAsset != null) { EditorApplication.Exit(0); return; }
        ImportEssentials();
        double deadline = EditorApplication.timeSinceStartup + 120;
        EditorApplication.update += WaitForFont;
        void WaitForFont() {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (TMP_Settings.defaultFontAsset != null) { EditorApplication.update -= WaitForFont; EditorApplication.Exit(0); }
            else if (EditorApplication.timeSinceStartup > deadline) { EditorApplication.update -= WaitForFont; EditorApplication.Exit(1); }
        }
    }
}
