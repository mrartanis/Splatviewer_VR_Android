// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // Independent Spark pipeline for static SHARP scenes. It never allocates the
    // legacy renderer's view-data, SH, GPU radix-sort or intermediate eye buffers.
    public sealed class SparkSplatRenderer : MonoBehaviour
    {
        internal static readonly HashSet<SparkSplatRenderer> Active = new();
        public GaussianSplatRenderer visibilityOwner;
        public bool HasScene => _data != null;
        public bool Visible => isActiveAndEnabled && HasScene &&
            (visibilityOwner == null || !visibilityOwner.m_SuspendRendering);
        public Bounds SceneBounds => _data?.Bounds ?? default;
        public int Count => _data?.Count ?? 0;
        public float LastSortMilliseconds { get; private set; }
        SparkSplatData _data;
        SparkSorter _sorter;
        GraphicsBuffer _packed, _order, _quad;
        Material _material;
        MaterialPropertyBlock _properties;
        CancellationTokenSource _cancellation;
        Task<SortResult> _sorting;
        Matrix4x4 _sortedTransform;
        Vector3 _sortedEye;
        int _drawCount;
        bool _alignOnRender;
        int _loggedPasses;
        struct SortResult { public int count; public float milliseconds; }

        void OnEnable() => Active.Add(this);
        void OnDisable() => Active.Remove(this);
        void OnDestroy() => Clear();

        public void SetScene(SparkSplatData data, SparkSorter initialSort, Shader shader, bool alignToHead = true)
        {
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Spark shader unavailable");
            Clear();
            _properties ??= new MaterialPropertyBlock();
            _data = data;
            _sorter = initialSort;
            _cancellation = new CancellationTokenSource();
            _packed = new GraphicsBuffer(GraphicsBuffer.Target.Structured, data.Count, 16);
            _packed.SetData(data.Packed);
            _order = new GraphicsBuffer(GraphicsBuffer.Target.Structured, data.Count, 4);
            _order.SetData(initialSort.Order);
            _quad = new GraphicsBuffer(GraphicsBuffer.Target.Index, 6, sizeof(ushort));
            _quad.SetData(new ushort[] { 0, 1, 2, 0, 2, 3 });
            _drawCount = data.Count;
            _material = new Material(shader) { name = "Spark Native PackedSplats" };
            _properties.SetBuffer("_SparkPacked", _packed);
            _properties.SetBuffer("_SparkOrder", _order);
            _alignOnRender = alignToHead;
            _loggedPasses = 0;
            if (isActiveAndEnabled) Active.Add(this);
            Debug.Log($"[SparkNative] Loaded {data.Count:N0} splats; GPU scene+order {(long)data.Count*20/1048576f:F1} MiB; direct eye rendering; radial worker sort");
        }

        public void Clear()
        {
            Active.Remove(this);
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            _sorting = null;
            _data = null;
            _sorter = null;
            _packed?.Dispose(); _packed = null;
            _order?.Dispose(); _order = null;
            _quad?.Dispose(); _quad = null;
            if (_material != null)
            {
                if (Application.isPlaying) Destroy(_material);
                else DestroyImmediate(_material);
            }
            _material = null;
            _drawCount = 0;
        }

        // Capture the pose used for rendering after tracking has updated, once.
        // The same object-to-world transform is retained on all following frames.
        internal void Prepare(Camera camera, Matrix4x4 renderView)
        {
            if (_alignOnRender)
            {
                Matrix4x4 inverse = renderView.inverse;
                Vector3 eye = inverse.GetColumn(3);
                if (camera.stereoEnabled)
                {
                    Vector3 left = camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left).inverse.GetColumn(3);
                    Vector3 right = camera.GetStereoViewMatrix(Camera.StereoscopicEye.Right).inverse.GetColumn(3);
                    eye = (left + right) * 0.5f;
                }
                // View-space cameras look down -Z; Unity Transform.forward is +Z.
                transform.SetPositionAndRotation(eye,
                    Quaternion.LookRotation(-inverse.GetColumn(2), inverse.GetColumn(1)));
                transform.localScale = new Vector3(1, -1, 1);
                _sortedEye = eye;
                _sortedTransform = transform.localToWorldMatrix;
                _alignOnRender = false;
                Debug.Log($"[SparkNative] Fixed capture origin={eye:F4}, forward={transform.forward:F4}");
            }
            PumpSort(camera.transform.position);
        }

        void PumpSort(Vector3 eye)
        {
            if (_sorting != null)
            {
                if (!_sorting.IsCompleted) return;
                try
                {
                    SortResult result = _sorting.GetAwaiter().GetResult();
                    _drawCount = result.count;
                    _order.SetData(_sorter.Order, 0, 0, _drawCount);
                    LastSortMilliseconds = result.milliseconds;
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { Debug.LogException(error); }
                _sorting = null;
            }
            Matrix4x4 matrix = transform.localToWorldMatrix;
            if ((eye - _sortedEye).sqrMagnitude < 0.000001f && matrix == _sortedTransform) return;
            _sortedEye = eye;
            _sortedTransform = matrix;
            var data = _data;
            var sorter = _sorter;
            CancellationToken token = _cancellation.Token;
            _sorting = Task.Run(() =>
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                int count = sorter.Sort(data.Centers, matrix, eye, token);
                return new SortResult { count = count, milliseconds = (float)timer.Elapsed.TotalMilliseconds };
            }, token);
        }

#if GS_ENABLE_URP
        internal void Draw(RasterCommandBuffer command, int width, int height, int eyePass)
        {
            if (!Visible || _drawCount == 0) return;
            _properties.SetMatrix("_SparkLocalToWorld", transform.localToWorldMatrix);
            _properties.SetVector("_SparkRenderSize", new Vector4(width, height, 0, 0));
            // The raster pass inherits URP's actual eye view/projection and viewport.
            command.DrawProcedural(_quad, Matrix4x4.identity, _material, 0, MeshTopology.Triangles, 6, _drawCount, _properties);
            if (_loggedPasses++ < 2)
                Debug.Log($"[SparkNative] Drawing eye {eyePass} directly at {width}x{height}, {_drawCount:N0} splats");
        }
#endif
    }
}
