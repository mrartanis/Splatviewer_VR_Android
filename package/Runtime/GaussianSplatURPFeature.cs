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
        bool m_HasCamera;

        public override void Create()
        {
            m_SparkPass = new SparkRenderPass { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents };
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
                renderer.EnqueuePass(m_SparkPass);
            if (!m_HasCamera)
                return;
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass = null;
            m_SparkPass = null;
        }
    }
}

#endif // #if GS_ENABLE_URP
