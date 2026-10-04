using System;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

public static class PackageBootstrap
{
    private static AddRequest request;
    private static double deadline;
    public static void Install()
    {
        string path = Environment.GetEnvironmentVariable("MUSE_UNITY_PACKAGE");
        if (string.IsNullOrEmpty(path)) { EditorApplication.Exit(2); return; }
        request = Client.Add("file:" + path.Replace('\\', '/'));
        deadline = EditorApplication.timeSinceStartup + 300;
        EditorApplication.update += Poll;
    }
    private static void Poll()
    {
        if (!request.IsCompleted && EditorApplication.timeSinceStartup < deadline) return;
        EditorApplication.update -= Poll;
        if (!request.IsCompleted || request.Status != StatusCode.Success) { UnityEngine.Debug.LogError(request.Error?.message ?? "Package installation timed out"); EditorApplication.Exit(1); }
        else { UnityEngine.Debug.Log("Muse Unity package installed through UPM."); EditorApplication.Exit(0); }
    }
}
