using UnityEditor;
using UnityEngine;

/// <summary>
/// One-time generator for the hand-occlusion material. The runtime code loads
/// "HandOcclusionMaterial" from Resources; a hand-authored .mat cannot
/// reference the shader (its GUID is assigned by the Editor at import), so
/// this script creates the material properly the first time the GUI Editor
/// loads the project after the shader arrives. Idempotent; GUI-only —
/// batchmode builds never mutate assets.
/// </summary>
[InitializeOnLoad]
public static class HandOcclusionSetup
{
    private const string ShaderName = "KitchenGuide/HandOcclusion";
    private const string MaterialPath = "Assets/Resources/HandOcclusionMaterial.mat";

    static HandOcclusionSetup()
    {
        if (Application.isBatchMode) return;
        EditorApplication.delayCall += Ensure;
    }

    private static void Ensure()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (AssetDatabase.LoadAssetAtPath<Material>(MaterialPath) != null) return;

        Shader shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            Debug.LogWarning("[KitchenGuide] Shader not found (yet): " + ShaderName +
                             " — occlusion material not generated.");
            return;
        }

        if (!AssetDatabase.IsValidFolder("Assets/Resources"))
            AssetDatabase.CreateFolder("Assets", "Resources");

        var mat = new Material(shader) { name = "HandOcclusionMaterial" };
        AssetDatabase.CreateAsset(mat, MaterialPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[KitchenGuide] Generated " + MaterialPath);
    }
}
