using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;

[InitializeOnLoad]
public static class BuildSetup
{
    const string ScenePath = "Assets/GSTestScene.unity";
    const string WindowsReleaseVersion = "1.0";

    static BuildSetup()
    {
        var scenes = EditorBuildSettings.scenes;

        foreach (var s in scenes)
        {
            if (s.path == ScenePath)
                return; // already added
        }

        var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(scenes);
        list.Add(new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = list.ToArray();
        Debug.Log($"[BuildSetup] Added '{ScenePath}' to Build Settings scenes.");
    }

    [MenuItem("Tools/Splatviewer/Build Windows Release")]
    public static void BuildWindowsReleaseMenu()
    {
        BuildWindowsRelease();
    }

    [MenuItem("Tools/Splatviewer/Build Android APK")]
    public static void BuildAndroidApkMenu()
    {
        BuildPicoApk();
    }

    public static void BuildWindowsRelease()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string outputFolder = Path.Combine(projectRoot, "Release", WindowsReleaseVersion);
        Directory.CreateDirectory(outputFolder);

        string exePath = Path.Combine(outputFolder, "SplatViewer_VR.exe");
        string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
        if (scenes.Length == 0)
            scenes = new[] { ScenePath };

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = exePath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException($"Windows release build failed: {report.summary.result}");

        CopyAssociationHelpers(projectRoot, outputFolder);
        Debug.Log($"[BuildSetup] Windows release build completed: {exePath}");
    }

    public static void BuildAndroidApk()
    {
        BuildPicoApk();
    }

    [MenuItem("Tools/VRPhoto/Build Pico 4 APK")]
    public static void BuildPicoApk()
    {
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.mrartanis.vrphoto.pico");
        PlayerSettings.productName = "VRPhoto";
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.forceInternetPermission = true;
        EditorUserBuildSettings.buildAppBundle = false;

        FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
        var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        if (settings == null)
            throw new BuildFailedException("Android OpenXR settings are missing");
        // The native splat renderer computes camera-space splats once per render pass.
        // Single-pass instancing would send that projection to both eyes.
        settings.renderMode = OpenXRSettings.RenderMode.MultiPass;
        EditorUtility.SetDirty(settings);
        bool picoSupport = false;
        bool picoController = false;
        foreach (var feature in settings.GetFeatures<OpenXRFeature>())
        {
            string type = feature.GetType().Name;
            if (type == "PICOFeature")
            {
                feature.enabled = true;
                picoSupport = true;
            }
            else if (type == "PICO4ControllerProfile")
            {
                feature.enabled = true;
                picoController = true;
            }
            else if (feature is OpenXRInteractionFeature || type.Contains("Quest") ||
                     type.Contains("Oculus") || type == "FoveatedRenderingFeature")
                feature.enabled = false;
            EditorUtility.SetDirty(feature);
        }
        if (!picoSupport || !picoController)
            throw new BuildFailedException("PICO OpenXR package or PICO 4 controller feature was not imported");
        AssetDatabase.SaveAssets();

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string outputFolder = Path.Combine(projectRoot, "Builds", "Pico");
        Directory.CreateDirectory(outputFolder);
        string apkPath = Path.Combine(outputFolder, "VRPhoto-Pico4.apk");
        string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
        if (scenes.Length == 0) scenes = new[] { ScenePath };
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = apkPath,
            target = BuildTarget.Android,
            options = BuildOptions.None,
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException($"Pico APK build failed: {report.summary.result}");
        Debug.Log($"[BuildSetup] Pico APK: {apkPath}");
    }

    static void CopyAssociationHelpers(string projectRoot, string outputFolder)
    {
        string repoRoot = Path.GetFullPath(Path.Combine(projectRoot, "..", ".."));
        string toolsFolder = Path.Combine(repoRoot, "tools");

        string[] helperFiles =
        {
            "Register-SplatviewerFileAssociations.ps1",
            "Register-SplatviewerFileAssociations.cmd",
        };

        foreach (string helperFile in helperFiles)
        {
            string sourcePath = Path.Combine(toolsFolder, helperFile);
            if (!File.Exists(sourcePath))
                continue;

            string destinationPath = Path.Combine(outputFolder, helperFile);
            File.Copy(sourcePath, destinationPath, true);
        }
    }
}
