using UnityEditor;
using UnityEngine;

public class AutoBuilder
{
    public static void BuildAndroid()
    {
        Quintar.StereoVideoCompositor.Editor.StereoCompositorBuildSetup.EnsureEnabledForAndroid();
        Quintar.StereoVideoCompositor.Editor.StereoCompositorSceneSetup.EnsureSceneAuthored();
        Quintar.StereoVideoCompositor.Editor.TestPurpleCircleSetup.EnsureInScene();

        string[] scenes = { "Assets/Scenes/MR_Passthrough_Scene.unity" };
        BuildPlayerOptions options = new BuildPlayerOptions();
        options.scenes = scenes;
        options.locationPathName = "Builds/MR_Passthrough.apk";
        options.target = BuildTarget.Android;
        options.options = BuildOptions.Development;

        System.IO.Directory.CreateDirectory("Builds");

        Debug.Log("Starting command line build to " + options.locationPathName);
        var report = BuildPipeline.BuildPlayer(options);
        
        if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            Debug.Log("Build succeeded: " + report.summary.totalSize + " bytes");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("Build failed!");
            EditorApplication.Exit(1);
        }
    }
}
