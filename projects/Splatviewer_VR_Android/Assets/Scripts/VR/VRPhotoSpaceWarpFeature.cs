// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

#if UNITY_EDITOR
[UnityEditor.XR.OpenXR.Features.OpenXRFeature(UiName = "VRPhoto experimental SpaceWarp",
    Desc = "Optional XR_FB_space_warp for the native SHARP viewer",
    Company = "VRPhoto", Version = "1.0.0", FeatureId = FeatureId,
    OpenxrExtensionStrings = "XR_FB_space_warp",
    BuildTargetGroups = new[] { UnityEditor.BuildTargetGroup.Android })]
#endif
public sealed class VRPhotoSpaceWarpFeature : OpenXRFeature
{
    public const string FeatureId = "com.mrartanis.vrphoto.spacewarp";
    public static bool Supported { get; private set; }
    public static bool SessionReady { get; private set; }
    protected override bool OnInstanceCreate(ulong instance)
    {
        Supported = OpenXRRuntime.IsExtensionEnabled("XR_FB_space_warp");
        Debug.Log($"[VRPhotoAppSW] Runtime extension supported={Supported}");
        return true;
    }
    protected override void OnSessionBegin(ulong session) => SessionReady = true;
    protected override void OnSessionEnd(ulong session) => SessionReady = false;
    protected override void OnInstanceDestroy(ulong instance) { Supported = false; SessionReady = false; }

    // OpenXR 1.13.1's native exports return XrResult (0 = success), not bool.
    // The motion-pass watchdog also verifies that the runtime actually supplies targets.
    [DllImport("UnityOpenXR", EntryPoint = "MetaSetSpaceWarp")]
    public static extern int SetEnabled([MarshalAs(UnmanagedType.I1)] bool enabled);
    [DllImport("UnityOpenXR", EntryPoint = "MetaSetAppSpacePosition")]
    static extern int SetPosition(float x, float y, float z);
    [DllImport("UnityOpenXR", EntryPoint = "MetaSetAppSpaceRotation")]
    static extern int SetRotation(float x, float y, float z, float w);
    public static bool SetOrigin(Transform origin)
    {
        Vector3 p = origin != null ? origin.position : Vector3.zero;
        Quaternion q = origin != null ? origin.rotation : Quaternion.identity;
        return SetPosition(p.x, p.y, p.z) >= 0 && SetRotation(q.x, q.y, q.z, q.w) >= 0;
    }
}
