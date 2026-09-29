// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

// Render the real catalog, address keyboard and certificate panel for visual review.
// Supply six JPEGs in Builds/Pico/catalog-previews before running PreviewAndBuild.
public static class CatalogPreview
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static FieldInfo Field(string name) => typeof(VRPhotoCatalog).GetField(name, Private);
    static void Call(VRPhotoCatalog catalog, string method) => typeof(VRPhotoCatalog).GetMethod(method, Private).Invoke(catalog, null);

    public static void PreviewAndBuild()
    {
        Run();
        BuildSetup.BuildPicoApk();
    }

    public static void Run()
    {
        string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../Builds/Pico/catalog-previews"));
        string[] files = Directory.GetFiles(output, "*.jpg");
        if (files.Length < 6) throw new Exception("Catalog preview needs six JPEGs");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var camera = new GameObject("Preview camera").AddComponent<Camera>();
        camera.tag = "MainCamera";
        camera.enabled = true;
        camera.orthographic = true;
        camera.orthographicSize = 0.75f;
        camera.aspect = 4f / 3f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.012f, 0.018f, 0.028f);
        camera.nearClipPlane = 0.01f;
        camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
        var pipeline = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
        float originalScale = pipeline.renderScale;
        int originalMsaa = pipeline.msaaSampleCount;
        int originalQualityMsaa = QualitySettings.antiAliasing;
        var catalog = new GameObject("Catalog preview").AddComponent<VRPhotoCatalog>();
        Call(catalog, "Awake");
        camera.enabled = false;
        Field("_origin").SetValue(catalog, "https://preview.invalid");
        var cache = (Dictionary<string, Texture2D>)Field("_thumbnailCache").GetValue(catalog);
        var textures = new List<Texture2D>();
        var scenes = (IList)Field("_scenes").GetValue(catalog);
        Type sceneType = typeof(VRPhotoCatalog).GetNestedType("SceneItem", BindingFlags.NonPublic);
        for (int i = 0; i < 6; i++)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGB24, true);
            ImageConversion.LoadImage(texture, File.ReadAllBytes(files[i]));
            texture.filterMode = FilterMode.Trilinear;
            textures.Add(texture);
            cache.Add($"https://preview.invalid/{i}?thumb=1&size=768", texture);
            object scene = Activator.CreateInstance(sceneType);
            sceneType.GetField("name").SetValue(scene, Path.GetFileNameWithoutExtension(files[i]));
            sceneType.GetField("path").SetValue(scene, i.ToString());
            sceneType.GetField("preview_url").SetValue(scene, $"/{i}?thumb=1");
            scenes.Add(scene);
        }
        Field("_library").SetValue(catalog, JsonUtility.FromJson("{\"path\":\"\",\"folders\":[],\"page\":1,\"pages\":1}",
            typeof(VRPhotoCatalog).GetNestedType("LibraryPage", BindingFlags.NonPublic)));
        Call(catalog, "BuildEntries");
        Field("_selected").SetValue(catalog, 5);
        ((Text)Field("_status").GetValue(catalog)).text = "Your photo collection";
        Call(catalog, "DrawEntries");
        var target = new RenderTexture(1600, 1200, 24, RenderTextureFormat.ARGB32);
        target.Create();
        var pixels = new Texture2D(1600, 1200, TextureFormat.RGB24, false);
        void Capture(string name)
        {
            Canvas.ForceUpdateCanvases();
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 1600, 1200), 0, 0);
            pixels.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(output, name + ".png"), pixels.EncodeToPNG());
        }
        Capture("library");
        Call(catalog, "BeginAddressEdit");
        Capture("address");
        Field("_mode").SetValue(catalog, Enum.Parse(Field("_mode").FieldType, "Pair"));
        Field("_candidateFingerprint").SetValue(catalog, new string('A', 64));
        ((Text)Field("_status").GetValue(catalog)).text = "Compare this SHA-256 fingerprint with the server log";
        Call(catalog, "BuildEntries");
        Capture("certificate");
        // Opening twice must not overwrite the viewer's saved quality settings.
        Call(catalog, "Show");
        Call(catalog, "Hide");
        if (pipeline.renderScale != originalScale || pipeline.msaaSampleCount != originalMsaa)
            throw new Exception("Catalog did not restore viewer quality");
        QualitySettings.antiAliasing = originalQualityMsaa;
        cache.Clear();
        foreach (var texture in textures) UnityEngine.Object.DestroyImmediate(texture);
        var sprite = (Sprite)Field("_roundedSprite").GetValue(catalog);
        Field("_roundedSprite").SetValue(catalog, null);
        UnityEngine.Object.DestroyImmediate(sprite.texture);
        UnityEngine.Object.DestroyImmediate(sprite);
        UnityEngine.Object.DestroyImmediate(catalog.gameObject);
        UnityEngine.Object.DestroyImmediate(camera.gameObject);
        UnityEngine.Object.DestroyImmediate(pixels);
        UnityEngine.Object.DestroyImmediate(target);
        Debug.Log("[CatalogPreview] Captured library, keyboard, certificate; viewer quality restored");
    }
}
