using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Headless build entry point for vrbuild.sh.
/// Builds the Meta Quest build profile to Builds/KitchenGuide.apk and
/// exits the batchmode Editor with a matching process exit code.
/// </summary>
public static class VrBuild
{
    private const string ProfilePath = "Assets/Settings/Build Profiles/Meta Quest.asset";
    private const string OutputPath = "Builds/KitchenGuide.apk";

    public static void BuildQuest()
    {
        var profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(ProfilePath);
        if (profile == null)
        {
            Debug.LogError("[VrBuild] Build profile not found at: " + ProfilePath);
            EditorApplication.Exit(1);
            return;
        }

        var options = new BuildPlayerWithProfileOptions
        {
            buildProfile = profile,
            locationPathName = OutputPath,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;
        Debug.Log("[VrBuild] Result: " + summary.result + " | size: " + summary.totalSize +
                  " bytes | errors: " + summary.totalErrors + " | time: " + summary.totalTime);
        EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
