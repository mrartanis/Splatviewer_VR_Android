// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Text;
using GaussianSplatting.Runtime;
using Unity.Mathematics;
using UnityEditor.SceneManagement;
using UnityEditor;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class SparkNativeValidation
{
    [Serializable] class Cases { public Case[] cases; }
    [Serializable] class Case { public float[] center, logScale, quaternion, rgba; public uint[] packed; }
    static float3 V3(float[] a) => new float3(a[0], a[1], a[2]);
    static float4 V4(float[] a) => new float4(a[0], a[1], a[2], a[3]);
    static void Check(bool ok, string message) { if (!ok) throw new Exception("Spark validation: " + message); }

    public static void ValidateAndBuild()
    {
        Run();
        BuildSetup.BuildPicoApk();
    }

    public static void Run()
    {
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        var vectors = JsonUtility.FromJson<Cases>(File.ReadAllText(Path.Combine(root,"third_party/spark/encoding-vectors.json")));
        for (int i=0; i<vectors.cases.Length; i++)
        {
            var v = vectors.cases[i];
            uint4 encoded = SparkSplatData.Encode(V3(v.center), V3(v.logScale), V4(v.quaternion), V4(v.rgba));
            for (int word=0; word<4; word++)
                Check(encoded[word] == v.packed[word], $"upstream packing case {i}, word {word}: {encoded[word]:x8} != {v.packed[word]:x8}");
        }
        Debug.Log($"[SparkValidation] {vectors.cases.Length} upstream JS packing vectors passed");

        var centers = new[] { new float3(1,0,0), new float3(0,-3,0), new float3(0,0,2), new float3(-3,0,0) };
        var sort = new SparkSorter(centers.Length);
        Check(sort.Sort(centers, Matrix4x4.identity, Vector3.zero)==4,"sort dropped points");
        uint[] expected = {1,3,2,0};
        for (int i=0;i<4;i++) Check(sort.Order[i]==expected[i],"radial ordering or stable ties");
        sort.Sort(centers, Matrix4x4.TRS(new Vector3(3,2,1),Quaternion.Euler(13,72,-4),new Vector3(1,-1,1)),new Vector3(3,2,1));
        for (int i=0;i<4;i++) Check(sort.Order[i]==expected[i],"radial sort changed under rigid transform/reflection");
        ValidatePly();
        ValidateGpuProjection();
        ValidateMotionShader();
        Debug.Log("[SparkValidation] All native Spark validation passed");
    }

    static void ValidateMotionShader()
    {
        var camera = new GameObject("Motion validation camera").AddComponent<Camera>();
        camera.enabled = false;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = 100;
        camera.fieldOfView = 70;
        camera.aspect = 4f / 3f;
        var target = new RenderTexture(640, 480, 0, RenderTextureFormat.ARGBFloat);
        target.Create();
        var pixels = new Texture2D(1, 1, TextureFormat.RGBAFloat, false);
        var material = new Material(Resources.Load<Shader>("SparkNative"));
        using var packed = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16);
        using var order = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4);
        using var quad = new GraphicsBuffer(GraphicsBuffer.Target.Index, 6, 2);
        packed.SetData(new[] { SparkSplatData.Encode(new float3(0,0,2),new float3(-2),new float4(0,0,0,1),new float4(1)) });
        order.SetData(new uint[] { 0 });
        quad.SetData(new ushort[] { 0,1,2,0,2,3 });
        var props = new MaterialPropertyBlock();
        props.SetBuffer("_SparkPacked", packed); props.SetBuffer("_SparkOrder", order);
        props.SetMatrix("_SparkLocalToWorld", Matrix4x4.identity);
        props.SetMatrix("_SparkPreviousFromCurrent", Matrix4x4.identity);
        props.SetVector("_SparkRenderSize", new Vector4(640,480,0,0));
        props.SetFloat("_SparkMotionY", 1);
        Matrix4x4 projection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
        var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/Medium_PipelineAsset_ForwardRenderer.asset");
        var feature = ScriptableObject.CreateInstance<SparkMotionValidationFeature>();
        feature.Material = material; feature.Properties = props; feature.Quad = quad;
        feature.Target = RTHandles.Alloc(target);
        rendererData.rendererFeatures.Add(feature); rendererData.SetDirty();
        var dummy = new RenderTexture(640,480,24); dummy.Create();
        try
        {
            foreach (float oldCameraX in new[] { 0f, 0.1f, -0.1f })
            {
                camera.transform.position = new Vector3(oldCameraX,0,0);
                Matrix4x4 previousVP = projection * camera.worldToCameraMatrix;
                props.SetMatrixArray("_SparkPreviousVP", new[] { previousVP, previousVP });
                camera.transform.position = Vector3.zero;
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination=dummy });
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(320,240,1,1),0,0); pixels.Apply();
                RenderTexture.active = null;
                Color motion = pixels.GetPixel(0,0);
                float expected = projection.m00 * oldCameraX / 2;
                Check(Mathf.Abs(motion.r-expected)<0.002f && Mathf.Abs(motion.g)<0.002f && Mathf.Abs(motion.b)<0.002f,
                    $"motion vector sign/magnitude: {motion} != {expected}");
            }
            Debug.Log("[SparkValidation] GPU motion pass: stationary and both camera translation directions passed");
        }
        finally
        {
            rendererData.rendererFeatures.Remove(feature); rendererData.SetDirty();
            feature.Target.Release();
            UnityEngine.Object.DestroyImmediate(feature);
            dummy.Release(); UnityEngine.Object.DestroyImmediate(dummy);
            UnityEngine.Object.DestroyImmediate(camera.gameObject);
            UnityEngine.Object.DestroyImmediate(material);
            UnityEngine.Object.DestroyImmediate(pixels);
            target.Release(); UnityEngine.Object.DestroyImmediate(target);
        }
    }

    static void ValidatePly()
    {
        string path = Path.Combine(Path.GetTempPath(), "vrphoto-spark-"+Guid.NewGuid().ToString("N")+".ply");
        try
        {
            const string header = "ply\nformat binary_little_endian 1.0\nelement vertex 1\nproperty float x\nproperty float y\nproperty float z\nproperty float f_dc_0\nproperty float f_dc_1\nproperty float f_dc_2\nproperty float opacity\nproperty float scale_0\nproperty float scale_1\nproperty float scale_2\nproperty float rot_0\nproperty float rot_1\nproperty float rot_2\nproperty float rot_3\nend_header\n";
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes(header));
                foreach (float f in new float[] {1,2,3,0,0,0,0,-4,-4,-4,1,0,0,0}) writer.Write(f);
            }
            var data = SparkSplatData.ReadPly(path);
            Check(data.Count==1 && math.all(data.Centers[0]==new float3(1,2,3)),"PLY coordinates were mirrored");
            using (var stream = File.OpenWrite(path)) stream.SetLength(stream.Length-1);
            bool rejected = false;
            try { SparkSplatData.ReadPly(path); } catch (InvalidDataException) { rejected=true; }
            Check(rejected,"truncated PLY accepted");
        }
        finally { File.Delete(path); }
        Debug.Log("[SparkValidation] PLY coordinates, truncation, stable radial sort passed");
    }

    static void ValidateGpuProjection()
    {
        Check(SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null,"GPU validation requires a graphics device (omit -nographics)");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var cameraObject = new GameObject("Spark validation camera");
        var camera = cameraObject.AddComponent<Camera>();
        camera.enabled = false;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = 100;
        camera.fieldOfView = 80;
        camera.aspect = 4f/3f;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
        var sceneObject = new GameObject("Spark validation splat");
        var renderer = sceneObject.AddComponent<SparkSplatRenderer>();
        float3 center = new float3(0.21f,0.12f,2f);
        var data = new SparkSplatData(new[] { SparkSplatData.Encode(center,new float3(-4),new float4(0,0,0,1),new float4(1)) },
            new[] {center},new Bounds(center,Vector3.one));
        var sorter = new SparkSorter(1);
        sorter.Sort(data.Centers,Matrix4x4.identity,Vector3.zero);
        renderer.SetScene(data,sorter,Resources.Load<Shader>("SparkNative"),false);
        sceneObject.transform.localScale = new Vector3(1,-1,1);
        var target = new RenderTexture(640,480,24,RenderTextureFormat.ARGB32);
        target.Create();
        var pixels = new Texture2D(640,480,TextureFormat.RGB24,false);
        string output = Path.GetFullPath(Path.Combine(Application.dataPath,"../Builds/Pico/spark-validation"));
        Directory.CreateDirectory(output);
        try
        {
            int frame = 0;
            foreach (float yaw in new[] {0f,-15f,15f})
            foreach (float eye in new[] {-0.033f,0.033f})
            {
                camera.transform.SetPositionAndRotation(new Vector3(eye,0,0),Quaternion.Euler(0,yaw,0));
                var projection = Matrix4x4.Perspective(80,4f/3f,0.01f,100);
                projection.m02 = eye < 0 ? 0.06f : -0.06f;
                camera.projectionMatrix = projection;
                RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest { destination=target });
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0,0,640,480),0,0);
                pixels.Apply();
                RenderTexture.active = null;
                double sum=0,xSum=0,ySum=0;
                var colors = pixels.GetPixels32();
                for(int i=0;i<colors.Length;i++)
                {
                    double weight=colors[i].r;
                    sum+=weight; xSum+=(i%640+0.5)*weight; ySum+=(i/640+0.5)*weight;
                }
                File.WriteAllBytes(Path.Combine(output,$"eye-{frame++}.png"),pixels.EncodeToPNG());
                Check(sum>100,"native shader rendered no visible splat");
                Vector3 expected=camera.WorldToViewportPoint(sceneObject.transform.TransformPoint(center));
                double dx=xSum/sum-expected.x*640,dy=ySum/sum-expected.y*480;
                Check(Math.Abs(dx)<1.5 && Math.Abs(dy)<1.5,$"GPU world projection yaw={yaw},eye={eye}: error=({dx:F3},{dy:F3}) px");
                Debug.Log($"[SparkValidation] GPU yaw={yaw}, eye={eye}, center error=({dx:F3},{dy:F3}) px");
            }
            // Regression: the scene used to cut off everything within 30 cm.
            // Match the web viewer's 1 cm near plane, and retain real near clipping.
            camera.ResetProjectionMatrix();
            Vector3 worldCenter = sceneObject.transform.TransformPoint(center);
            foreach (float distance in new[] { 0.2f, 0.02f, 0.005f, -0.02f })
            {
                camera.transform.SetPositionAndRotation(worldCenter - Vector3.forward * distance, Quaternion.identity);
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination=target });
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0,0,640,480),0,0); pixels.Apply();
                RenderTexture.active = null;
                long sum = 0;
                foreach (var pixel in pixels.GetPixels32()) sum += pixel.r;
                Check(distance > 0.01f ? sum > 100 : sum == 0, $"near clipping at {distance}m failed: {sum}");
            }
            Debug.Log("[SparkValidation] Close-up visible at 20cm and 2cm, clipped below 1cm and behind camera");
            string[] args = Environment.GetCommandLineArgs();
            int plyArgument = Array.IndexOf(args,"-sparkPly");
            if (plyArgument >= 0 && plyArgument+1 < args.Length)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var real = SparkSplatData.ReadPly(args[plyArgument+1]);
                var realSort = new SparkSorter(real.Count);
                Check(realSort.Sort(real.Centers,Matrix4x4.identity,Vector3.zero)==real.Count,"real scene lost splats");
                renderer.SetScene(real,realSort,Resources.Load<Shader>("SparkNative"),false);
                sceneObject.transform.localScale = new Vector3(1,-1,1);
                camera.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);
                camera.ResetProjectionMatrix();
                RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest { destination=target });
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0,0,640,480),0,0); pixels.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(output,"sharp-scene.png"),pixels.EncodeToPNG());
                Debug.Log($"[SparkValidation] Real SHARP scene: {real.Count:N0} splats, read/pack/sort/draw {timer.ElapsedMilliseconds} ms");
            }
        }
        finally
        {
            renderer.Clear();
            UnityEngine.Object.DestroyImmediate(sceneObject);
            UnityEngine.Object.DestroyImmediate(cameraObject);
            UnityEngine.Object.DestroyImmediate(pixels);
            target.Release(); UnityEngine.Object.DestroyImmediate(target);
        }
    }
}

// Run the real motion shader through URP's camera constant buffers, as on device.
public sealed class SparkMotionValidationFeature : ScriptableRendererFeature
{
    public RTHandle Target;
    public Material Material;
    public MaterialPropertyBlock Properties;
    public GraphicsBuffer Quad;
    TestPass _pass;
    public override void Create() => _pass = new TestPass { Owner=this, renderPassEvent=RenderPassEvent.AfterRenderingTransparents };
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData data) => renderer.EnqueuePass(_pass);
    sealed class TestPass : ScriptableRenderPass
    {
        public SparkMotionValidationFeature Owner;
        sealed class Data { public SparkMotionValidationFeature Owner; }
        public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
        {
            var color = graph.ImportTexture(Owner.Target, new ImportResourceParams { clearOnFirstUse=true, clearColor=Color.clear });
            var depth = graph.CreateTexture(new TextureDesc(640,480) { depthBufferBits=DepthBits.Depth24, clearBuffer=true, name="Motion validation depth" });
            using var builder = graph.AddRasterRenderPass<Data>("Motion validation", out var data);
            data.Owner=Owner;
            builder.SetRenderAttachment(color,0,AccessFlags.Write);
            builder.SetRenderAttachmentDepth(depth,AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc(static (Data d, RasterGraphContext ctx) =>
                ctx.cmd.DrawProcedural(d.Owner.Quad,Matrix4x4.identity,d.Owner.Material,1,MeshTopology.Triangles,6,1,d.Owner.Properties));
        }
    }
}
