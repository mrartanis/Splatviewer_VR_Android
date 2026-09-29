// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.XR;

/// <summary>Optional application frame counter; generated compositor frames are not counted.</summary>
public sealed class VRPhotoPerformanceHud : MonoBehaviour
{
    const string Preference = "vrphoto-show-fps";
    public static VRPhotoPerformanceHud Instance { get; private set; }
    public bool Visible { get; private set; }
    readonly List<XRDisplaySubsystem> _displays = new();
    GameObject _panel;
    Text _text;
    Material _material;
    double _sampleStart;
    int _frames;

    void Awake()
    {
        Instance = this;
        Visible = PlayerPrefs.GetInt(Preference, 0) != 0;
        ResetSample();
    }

    public void SetVisible(bool visible)
    {
        Visible = visible;
        PlayerPrefs.SetInt(Preference, visible ? 1 : 0);
        PlayerPrefs.Save();
        if (_panel != null) _panel.SetActive(visible);
        ResetSample();
        Debug.Log($"[VRPhotoFPS] Overlay {(visible ? "on" : "off")}");
    }

    void ResetSample()
    {
        _sampleStart = Time.realtimeSinceStartupAsDouble;
        _frames = 0;
        if (_text != null) _text.text = "App FPS: measuring...";
    }

    void OnApplicationPause(bool paused) => ResetSample();
    void OnApplicationFocus(bool focused) => ResetSample();

    void Update()
    {
        if (!Visible) return;
        if (_panel == null)
        {
            var camera = Camera.main;
            if (camera == null) return;
            BuildPanel(camera);
            ResetSample();
            return;
        }
        ++_frames;
        double elapsed = Time.realtimeSinceStartupAsDouble - _sampleStart;
        if (elapsed < 0.5) return;
        double fps = _frames / elapsed;
        string refresh = "--";
        SubsystemManager.GetSubsystems(_displays);
        foreach (var display in _displays)
            if (display.running && display.TryGetDisplayRefreshRate(out float hz))
            {
                refresh = hz.ToString("0");
                break;
            }
        _text.text = $"App FPS  {fps:0.0}   |   {elapsed * 1000 / _frames:0.0} ms\n" +
            $"Display {refresh} Hz   |   AppSW {VRPhotoFrameGeneration.Instance?.Status ?? "Off"}";
        _sampleStart = Time.realtimeSinceStartupAsDouble;
        _frames = 0;
    }

    void BuildPanel(Camera camera)
    {
        _panel = new GameObject("Performance HUD", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        _panel.transform.SetParent(camera.transform, false);
        _panel.transform.localPosition = new Vector3(0.30f, -0.24f, 1f);
        _panel.transform.localScale = Vector3.one * 0.001f;
        _panel.GetComponent<RectTransform>().sizeDelta = new Vector2(440, 92);
        var canvas = _panel.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = camera;
        canvas.sortingOrder = 32760;
        _panel.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 3;
        _material = new Material(Shader.Find("UI/Default"));
        _material.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
        var background = _panel.AddComponent<Image>();
        background.color = new Color(0.025f, 0.035f, 0.045f, 0.88f);
        background.material = _material;
        background.raycastTarget = false;

        var label = new GameObject("Counters", typeof(RectTransform));
        label.transform.SetParent(_panel.transform, false);
        var rect = label.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(14, 8);
        rect.offsetMax = new Vector2(-14, -8);
        _text = label.AddComponent<Text>();
        _text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        _text.fontSize = 24;
        _text.color = new Color(0.75f, 0.95f, 1f);
        _text.alignment = TextAnchor.MiddleLeft;
        _text.horizontalOverflow = HorizontalWrapMode.Overflow;
        _text.material = _material;
        _text.raycastTarget = false;
    }

    void OnDestroy()
    {
        if (_panel != null) Destroy(_panel);
        if (_material != null) Destroy(_material);
        if (Instance == this) Instance = null;
    }
}
