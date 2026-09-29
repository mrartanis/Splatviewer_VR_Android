// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

#if !UNITY_6000_0_OR_NEWER
#error Unity Gaussian Splatting URP support only works in Unity 6 or later
#endif

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace GaussianSplatting.Runtime
{
    // Note: I have no idea what is the purpose of ScriptableRendererFeature vs ScriptableRenderPass, which one of those
    // is supposed to do resource management vs logic, etc. etc. Code below "seems to work" but I'm just fumbling along,
    // without understanding any of it.
    //
    // ReSharper disable once InconsistentNaming
    class GaussianSplatURPFeature : ScriptableRendererFeature
    {
        // Spark owns the final eye rasterization. Using a raster attachment makes
        // URP set its viewport and XR matrices, avoiding the legacy scaled RT/blit.
        class SparkRenderPass : ScriptableRenderPass
        {
            class PassData
            {
                internal SparkSplatRenderer[] Renderers;
                internal int Width, Height, Eye;
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                var camera = frameData.Get<UniversalCameraData>();
                if (camera.camera.cameraType == CameraType.Preview) return;
                var visible = new System.Collections.Generic.List<SparkSplatRenderer>();
                foreach (var renderer in SparkSplatRenderer.Active)
                    if (renderer != null && renderer.Visible)
                    {
                        renderer.Prepare(camera.camera, camera.GetViewMatrix(0));
                        visible.Add(renderer);
                    }
                if (visible.Count == 0) return;
                var resources = frameData.Get<UniversalResourceData>();
                using var builder = graph.AddRasterRenderPass<PassData>("Spark Native Direct Eyes", out var data);
                data.Renderers = visible.ToArray();
                data.Width = camera.cameraTargetDescriptor.width;
                data.Height = camera.cameraTargetDescriptor.height;
                data.Eye = camera.xr.enabled ? camera.xr.multipassId : -1;
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData pass, RasterGraphContext context) =>
                {
                    foreach (var renderer in pass.Renderers)
                        if (renderer != null) renderer.Draw(context.cmd, pass.Width, pass.Height, pass.Eye);
                });
            }
        }

        // Add procedural splats to the XR runtime's motion/depth swapchain after
        // URP's ordinary mesh motion pass. No extra full-resolution color render.
        class SparkMotionPass : ScriptableRenderPass
        {
            RTHandle _motion, _depth;
            Matrix4x4[] _lastVP = new Matrix4x4[2];
            int _lastFrame = -2;
            class PassData
            {
                internal SparkSplatRenderer[] Renderers;
                internal Matrix4x4[] PreviousVP;
                internal int Width, Height;
                internal float MotionY;
            }
            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                if (!SparkSplatRenderer.MotionRequested) { _lastFrame = -2; return; }
                var camera = frameData.Get<UniversalCameraData>();
                if (!camera.xr.enabled || !camera.xr.singlePassEnabled || !camera.xr.hasMotionVectorPass) return;
                var renderers = new System.Collections.Generic.List<SparkSplatRenderer>();
                foreach (var renderer in SparkSplatRenderer.Active)
                    if (renderer != null && renderer.Visible) renderers.Add(renderer);
                if (renderers.Count == 0) return;
                var descriptor = camera.xr.motionVectorRenderTargetDesc;
                if (descriptor.width < 1 || descriptor.height < 1) return;
                var target = camera.xr.motionVectorRenderTarget;
                if (_motion == null) _motion = RTHandles.Alloc(target);
                else RTHandleStaticHelpers.SetRTHandleUserManagedWrapper(ref _motion, target);
                if (_depth == null) _depth = RTHandles.Alloc(target);
                else RTHandleStaticHelpers.SetRTHandleUserManagedWrapper(ref _depth, target);
                var info = new RenderTargetInfo
                {
                    width = descriptor.width, height = descriptor.height,
                    volumeDepth = descriptor.volumeDepth, msaaSamples = descriptor.msaaSamples,
                    format = descriptor.graphicsFormat
                };
                var keep = new ImportResourceParams { clearOnFirstUse = false, discardOnLastUse = false };
                var color = graph.ImportTexture(_motion, info, keep);
                info.format = descriptor.depthStencilFormat;
                var depth = graph.ImportTexture(_depth, info, keep);
                var current = new Matrix4x4[2];
                for (int eye = 0; eye < 2; eye++)
                    current[eye] = GL.GetGPUProjectionMatrix(camera.xr.GetProjMatrix(eye), false) * camera.GetViewMatrix(eye);
                var previous = _lastFrame == Time.frameCount - 1 ? _lastVP : current;
                _lastVP = current;
                _lastFrame = Time.frameCount;
                using var builder = graph.AddRasterRenderPass<PassData>("Spark XR motion and depth", out var data);
                data.Renderers = renderers.ToArray();
                data.PreviousVP = previous;
                data.Width = camera.cameraTargetDescriptor.width;
                data.Height = camera.cameraTargetDescriptor.height;
                data.MotionY = camera.xr.spaceWarpRightHandedNDC ? -1f : 1f;
                builder.SetRenderAttachment(color, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(depth, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData pass, RasterGraphContext context) =>
                {
                    foreach (var renderer in pass.Renderers)
                        if (renderer != null) renderer.DrawMotion(context.cmd, pass.Width, pass.Height, pass.PreviousVP, pass.MotionY);
                });
            }
            public void Release() { _motion?.Release(); _depth?.Release(); _motion = _depth = null; }
        }

        class GSRenderPass : ScriptableRenderPass
        {
            const string GaussianSplatRTName = "_GaussianSplatRT";
            const float MobileSplatRenderScale = 0.70f;

            const string ProfilerTag = "GaussianSplatRenderGraph";
            static readonly ProfilingSampler s_profilingSampler = new(ProfilerTag);
            static readonly int s_gaussianSplatRT = Shader.PropertyToID(GaussianSplatRTName);
            static int s_loggedEyePasses;

            class PassData
            {
                internal UniversalCameraData CameraData;
                internal TextureHandle SourceTexture;
                internal TextureHandle GaussianSplatRT;
                internal int SplatWidth;
                internal int SplatHeight;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                using var builder = renderGraph.AddUnsafePass(ProfilerTag, out PassData passData);

                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();
                if (cameraData.xr.enabled && s_loggedEyePasses < 6)
                {
                    Matrix4x4 view = cameraData.GetViewMatrix(0);
                    Vector3 eye = view.inverse.GetColumn(3);
                    Debug.Log($"[VRPhotoStereo] pass={cameraData.xr.multipassId} eye={eye:F4} projectionX={cameraData.GetProjectionMatrix(0).m02:F4}");
                    s_loggedEyePasses++;
                }

                RenderTextureDescriptor rtDesc = cameraData.cameraTargetDescriptor;
                rtDesc.depthBufferBits = 0;
                rtDesc.msaaSamples = 1;
                if (Application.isMobilePlatform && UnityEngine.XR.XRSettings.isDeviceActive)
                {
                    rtDesc.width = Mathf.Max(1, Mathf.RoundToInt(rtDesc.width * MobileSplatRenderScale));
                    rtDesc.height = Mathf.Max(1, Mathf.RoundToInt(rtDesc.height * MobileSplatRenderScale));
                }
                rtDesc.graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm;
                var textureHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, rtDesc, GaussianSplatRTName, true);

                passData.CameraData = cameraData;
                passData.SourceTexture = resourceData.activeColorTexture;
                passData.GaussianSplatRT = textureHandle;
                passData.SplatWidth = rtDesc.width;
                passData.SplatHeight = rtDesc.height;

                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                builder.UseTexture(textureHandle, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    using var _ = new ProfilingScope(commandBuffer, s_profilingSampler);
                    commandBuffer.SetGlobalTexture(s_gaussianSplatRT, data.GaussianSplatRT);
                    CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT, ClearFlag.Color, Color.clear);
                    Material matComposite = GaussianSplatRenderSystem.instance.SortAndRenderSplats(data.CameraData.camera, commandBuffer,
                        data.SplatWidth, data.SplatHeight, data.CameraData.GetViewMatrix(0),
                        GL.GetGPUProjectionMatrix(data.CameraData.GetProjectionMatrix(0), true));
                    commandBuffer.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
                    Blitter.BlitCameraTexture(commandBuffer, data.GaussianSplatRT, data.SourceTexture, matComposite, 0);
                    commandBuffer.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
                });
            }
        }

        GSRenderPass m_Pass;
        SparkRenderPass m_SparkPass;
        SparkMotionPass m_MotionPass;
        bool m_HasCamera;

        public override void Create()
        {
            m_SparkPass = new SparkRenderPass { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents };
            m_MotionPass = new SparkMotionPass { renderPassEvent = RenderPassEvent.AfterRenderingTransparents };
            m_Pass = new GSRenderPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_HasCamera = false;
            var system = GaussianSplatRenderSystem.instance;
            if (!system.GatherSplatsForCamera(cameraData.camera))
                return;

            m_HasCamera = true;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (SparkSplatRenderer.Active.Count > 0)
            {
                renderer.EnqueuePass(m_SparkPass);
                if (SparkSplatRenderer.MotionRequested) renderer.EnqueuePass(m_MotionPass);
            }
            if (!m_HasCamera)
                return;
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass = null;
            m_SparkPass = null;
            m_MotionPass?.Release();
            m_MotionPass = null;
        }
    }
}

#endif // #if GS_ENABLE_URP
