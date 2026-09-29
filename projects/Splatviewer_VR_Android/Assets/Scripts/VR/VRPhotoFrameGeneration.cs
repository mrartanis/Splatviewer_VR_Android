// SPDX-License-Identifier: MIT
using System;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.XR.OpenXR;

public sealed class VRPhotoFrameGeneration : MonoBehaviour
{
    const string Preference = "vrphoto-experimental-frame-generation";
    public static VRPhotoFrameGeneration Instance { get; private set; }
    public bool Requested { get; private set; }
    public string Status { get; private set; } = "Off";
    bool _active, _failed;
    int _startedFrame;
    RuntimeSplatLoader _loader;
    VRRig _rig;

    void Awake()
    {
        Instance = this;
        Requested = PlayerPrefs.GetInt(Preference, 0) != 0;
        _loader = FindAnyObjectByType<RuntimeSplatLoader>();
        _rig = FindAnyObjectByType<VRRig>();
    }
    public void SetRequested(bool requested)
    {
        Requested = requested;
        _failed = false;
        PlayerPrefs.SetInt(Preference, requested ? 1 : 0);
        PlayerPrefs.Save();
        Update();
    }
    void Update()
    {
        bool available = Application.isMobilePlatform && VRPhotoSpaceWarpFeature.Supported;
        bool scene = _loader != null && _loader.NativeSpark != null && _loader.NativeSpark.Visible;
        bool wanted = Requested && available && !_failed && scene && VRPhotoSpaceWarpFeature.SessionReady;
        if (wanted != _active)
        {
            try
            {
                if (wanted)
                {
                    OpenXRSettings.Instance.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
                    if (VRPhotoSpaceWarpFeature.SetEnabled(true) < 0) throw new InvalidOperationException("Runtime rejected AppSW");
                    _startedFrame = Time.frameCount;
                    SparkSplatRenderer.LastMotionFrame = -1;
                }
                else
                {
                    VRPhotoSpaceWarpFeature.SetEnabled(false);
                    OpenXRSettings.Instance.renderMode = OpenXRSettings.RenderMode.MultiPass;
                }
                _active = wanted;
                SparkSplatRenderer.MotionRequested = wanted;
                Debug.Log($"[VRPhotoAppSW] requested={Requested}, active={_active}");
            }
            catch (Exception error) { Fail(error.Message); }
        }
        if (_active)
        {
            try
            {
                // App space is the tracking origin, not the tracked head pose.
                Transform origin = _rig != null ? (_rig.cameraOffset != null ? _rig.cameraOffset : _rig.transform) : null;
                if (!VRPhotoSpaceWarpFeature.SetOrigin(origin)) Fail("Origin update rejected");
                if (Time.frameCount - Math.Max(_startedFrame, SparkSplatRenderer.LastMotionFrame) > 90)
                    Fail("Runtime did not provide usable XR motion targets");
            }
            catch (Exception error) { Fail(error.Message); }
        }
        Status = !Requested ? "Off" : _failed || !available ? "Unavailable" :
            !_active ? "On / paused" : SparkSplatRenderer.LastMotionFrame >= _startedFrame ? "On (exp.)" : "Starting";
    }
    void Fail(string reason)
    {
        Debug.LogWarning("[VRPhotoAppSW] " + reason);
        _failed = true;
        _active = false;
        SparkSplatRenderer.MotionRequested = false;
        try { VRPhotoSpaceWarpFeature.SetEnabled(false); } catch (Exception) { }
        if (OpenXRSettings.Instance != null) OpenXRSettings.Instance.renderMode = OpenXRSettings.RenderMode.MultiPass;
    }
    void OnDestroy()
    {
        if (_active) Fail("Controller stopped");
        if (Instance == this) Instance = null;
    }
}
