using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

/// <summary>
/// One-time importer for TextMesh Pro Essential Resources. The project has
/// the TMP code (inside com.unity.ugui) but not the essential assets —
/// without them there is no default SDF font and all TMP text renders
/// broken. Detects the TMP Settings asset; if missing, finds the
/// "TMP Essential Resources" .unitypackage inside the ugui package and
/// imports it silently. GUI Editor only; idempotent.
/// </summary>
[InitializeOnLoad]
public static class TmpEssentialsBootstrap
{
    private const string SessionKey = "KitchenGuide.TmpImportTried";

    static TmpEssentialsBootstrap()
    {
        if (Application.isBatchMode) return;
        EditorApplication.delayCall += Ensure;
    }

    private static void Ensure()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (SessionState.GetBool(SessionKey, false)) return;
        SessionState.SetBool(SessionKey, true);

        if (AssetDatabase.FindAssets("t:TMP_Settings").Length > 0) return;

        PackageInfo info = PackageInfo.FindForAssetPath("Packages/com.unity.ugui");
        if (info == null || string.IsNullOrEmpty(info.resolvedPath))
        {
            Debug.LogWarning("[KitchenGuide] ugui package not found — cannot auto-import TMP Essentials.");
            return;
        }

        string[] packs = System.IO.Directory.GetFiles(
            info.resolvedPath, "*.unitypackage", System.IO.SearchOption.AllDirectories);
        foreach (string p in packs)
        {
            if (p.IndexOf("Essential", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Debug.Log("[KitchenGuide] Importing TMP Essential Resources: " + p);
                AssetDatabase.ImportPackage(p, false);
                return;
            }
        }
        Debug.LogWarning("[KitchenGuide] No TMP Essential Resources .unitypackage found under " + info.resolvedPath);
    }
}
