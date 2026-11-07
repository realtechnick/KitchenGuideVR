using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Opens the project's main scene automatically when the Editor GUI launches,
/// so work (e.g. Building Blocks) never lands in a default "Untitled" scene —
/// the failure mode hit on 2026-11-07, when a Hand Tracking block was installed
/// into a fresh untitled scene instead of SpikeScene.
///
/// Conservative by design:
/// - GUI launches only; batchmode (vrbuild) is untouched — builds take their
///   scenes from EditorBuildSettings, not from whatever is open.
/// - Once per Editor session (SessionState), so it never yanks the user back
///   after they deliberately open a different scene mid-session.
/// - Only replaces a PRISTINE untitled scene (no path, not dirty); a scene
///   with unsaved work is never stomped.
/// </summary>
[InitializeOnLoad]
public static class OpenMainSceneOnLoad
{
    private const string MainScenePath = "Assets/Scenes/SpikeScene.unity";
    private const string SessionKey = "KitchenGuide.AutoOpenedMainScene";

    static OpenMainSceneOnLoad()
    {
        if (Application.isBatchMode) return;
        if (SessionState.GetBool(SessionKey, false)) return;
        SessionState.SetBool(SessionKey, true);
        EditorApplication.delayCall += TryOpen;
    }

    private static void TryOpen()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(MainScenePath) == null) return;

        Scene active = SceneManager.GetActiveScene();
        bool pristineUntitled = active.IsValid() && active.path == "" && !active.isDirty;
        if (!pristineUntitled) return;

        EditorSceneManager.OpenScene(MainScenePath, OpenSceneMode.Single);
        Debug.Log("[KitchenGuide] Auto-opened " + MainScenePath + " (was: pristine Untitled).");
    }
}
