// SPDX-License-Identifier: MIT
// PackedSplats encoding and half-float bucket sort ported from World Labs Spark.
// Upstream attribution and pinned revision: third_party/spark/README.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public sealed class SparkSplatData
    {
        public readonly uint4[] Packed;
        public readonly float3[] Centers;
        public readonly Bounds Bounds;
        public int Count => Packed.Length;

        public SparkSplatData(uint4[] packed, float3[] centers, Bounds bounds)
        {
            Packed = packed;
            Centers = centers;
            Bounds = bounds;
        }

        // Read SHARP's original coordinates; the object-to-world transform performs
        // the OpenCV -> Unity reflection. No implicit X mirror or Morton reorder.
        public static SparkSplatData ReadPly(string path, CancellationToken cancellation = default)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.ASCII, true);
            string Line()
            {
                var bytes = new List<byte>();
                while (bytes.Count < 65536)
                {
                    int b = stream.ReadByte();
                    if (b < 0) throw new InvalidDataException("Truncated PLY header");
                    if (b == 10) return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
                    bytes.Add((byte)b);
                }
                throw new InvalidDataException("PLY header line too long");
            }
            if (Line() != "ply") throw new InvalidDataException("Not a PLY file");
            var properties = new Dictionary<string, int>();
            int count = 0, stride = 0;
            bool vertex = false, binary = false;
            for (int lines = 0; ; lines++)
            {
                if (lines > 4096) throw new InvalidDataException("PLY header too long");
                string line = Line();
                if (line == "end_header") break;
                var fields = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 0) continue;
                if (fields[0] == "format") binary = line == "format binary_little_endian 1.0";
                if (fields[0] == "element")
                {
                    if (fields.Length != 3) throw new InvalidDataException("Invalid PLY element");
                    vertex = fields[1] == "vertex";
                    if (vertex) count = int.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture);
                    else if (count == 0 && fields[2] != "0")
                        throw new InvalidDataException("PLY vertex data must be first");
                }
                if (fields[0] == "property" && vertex)
                {
                    if (fields.Length != 3 || (fields[1] != "float" && fields[1] != "float32"))
                        throw new InvalidDataException("Native SHARP PLY requires float32 vertex properties");
                    properties.Add(fields[2], stride++);
                }
            }
            if (!binary || count <= 0 || stride == 0 || stride > 256)
                throw new InvalidDataException("Unsupported or empty binary SHARP PLY");
            if ((long)count * stride * 4 > stream.Length - stream.Position)
                throw new InvalidDataException("Truncated PLY vertices");
            int Index(string name) => properties.TryGetValue(name, out int i) ? i :
                throw new InvalidDataException("Missing PLY property: " + name);
            int x = Index("x"), y = Index("y"), z = Index("z");
            int r = Index("f_dc_0"), g = Index("f_dc_1"), bIndex = Index("f_dc_2"), a = Index("opacity");
            int sx = Index("scale_0"), sy = Index("scale_1"), sz = Index("scale_2");
            int qw = Index("rot_0"), qx = Index("rot_1"), qy = Index("rot_2"), qz = Index("rot_3");
            if (properties.ContainsKey("f_rest_0"))
                throw new InvalidDataException("Use the local-file renderer for PLY with higher SH bands");
            var packed = new uint4[count];
            var centers = new float3[count];
            float3 minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
            const int blockSize = 4096;
            var bytesBlock = new byte[blockSize * stride * 4];
            var floats = new float[blockSize * stride];
            for (int start = 0; start < count; start += blockSize)
            {
                cancellation.ThrowIfCancellationRequested();
                int length = Math.Min(blockSize, count - start);
                int bytes = length * stride * 4, read = 0;
                while (read < bytes)
                {
                    int got = reader.Read(bytesBlock, read, bytes - read);
                    if (got == 0) throw new InvalidDataException("Truncated PLY vertices");
                    read += got;
                }
                Buffer.BlockCopy(bytesBlock, 0, floats, 0, bytes);
                for (int i = 0; i < length; i++)
                {
                    int o = i * stride;
                    float3 p = new float3(floats[o+x], floats[o+y], floats[o+z]);
                    float3 logScale = new float3(floats[o+sx], floats[o+sy], floats[o+sz]);
                    float4 q = new float4(floats[o+qx], floats[o+qy], floats[o+qz], floats[o+qw]);
                    float3 rgb = 0.5f + 0.28209479177387814f * new float3(floats[o+r], floats[o+g], floats[o+bIndex]);
                    float opacity = 1f / (1f + math.exp(-floats[o+a]));
                    if (!math.all(math.isfinite(p)) || math.any(math.abs(p) > 65504f) ||
                        !math.all(math.isfinite(logScale)) || !math.all(math.isfinite(q)) ||
                        !math.all(math.isfinite(rgb)) || !math.isfinite(opacity))
                        throw new InvalidDataException("Non-finite or unrepresentable PLY vertex " + (start+i));
                    float norm = math.length(q);
                    q = norm > 1e-10f ? q / norm : new float4(0, 0, 0, 1);
                    uint4 encoded = Encode(p, logScale, q, new float4(rgb, opacity));
                    packed[start+i] = encoded;
                    // Sort precisely the positions that the GPU decodes.
                    centers[start+i] = DecodeCenter(encoded);
                    minimum = math.min(minimum, p);
                    maximum = math.max(maximum, p);
                }
            }
            return new SparkSplatData(packed, centers, new Bounds((minimum+maximum)*0.5f, maximum-minimum));
        }

        static uint Byte(float value) => (uint)math.floor(math.clamp(value, 0f, 255f) + 0.5f);

        public static uint4 Encode(float3 center, float3 logScale, float4 q, float4 rgba)
        {
            if (q.w < 0) q = -q;
            float halfTheta = math.acos(math.clamp(q.w, -1f, 1f));
            float s = math.sin(halfTheta);
            float3 axis = math.abs(s) < 1e-6f ? new float3(1, 0, 0) : q.xyz/s;
            float2 oct = axis.xy / math.csum(math.abs(axis));
            if (axis.z < 0) oct = (1f - math.abs(oct.yx)) * math.select(-1f, 1f, oct >= 0);
            uint u = Byte((oct.x*0.5f+0.5f)*255f), v = Byte((oct.y*0.5f+0.5f)*255f);
            uint angle = Byte(halfTheta*2f/math.PI*255f);
            uint3 scale = (uint3)math.floor(math.clamp((logScale+12f)*(254f/21f), 0f, 254f)+0.5f)+1;
            scale = math.select(scale, new uint3(0), logScale < -30f);
            return new uint4(
                Byte(rgba.x*255f) | (Byte(rgba.y*255f)<<8) | (Byte(rgba.z*255f)<<16) | (Byte(rgba.w*255f)<<24),
                math.f32tof16(center.x) | (math.f32tof16(center.y)<<16),
                math.f32tof16(center.z) | (u<<16) | (v<<24),
                scale.x | (scale.y<<8) | (scale.z<<16) | (angle<<24));
        }

        public static float3 DecodeCenter(uint4 p) => new float3(math.f16tof32(p.y & 65535),
            math.f16tof32(p.y >> 16), math.f16tof32(p.z & 65535));
    }

    // Direct port of Spark sort_internal: finite positive f16 metric, descending
    // buckets, stable scatter. Static scenes can calculate metrics on the worker
    // directly, without Spark's WebGL readback needed for dynamic generators.
    public sealed class SparkSorter
    {
        readonly ushort[] _depth;
        readonly int[] _buckets = new int[0x7c01];
        public readonly uint[] Order;
        public SparkSorter(int count) { _depth = new ushort[count]; Order = new uint[count]; }

        public int Sort(float3[] centers, Matrix4x4 localToWorld, Vector3 eye, CancellationToken cancellation = default)
        {
            Array.Clear(_buckets, 0, _buckets.Length);
            for (int i = 0; i < centers.Length; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                Vector3 p = localToWorld.MultiplyPoint3x4(centers[i]) - eye;
                // Clamp overflow to the largest finite half rather than dropping
                // a valid faraway point. Rendering applies near/far clipping.
                uint metric = math.f32tof16(math.min(p.magnitude, 65504f));
                _depth[i] = (ushort)metric;
                if (metric < 0x7c00) _buckets[metric]++;
            }
            int active = 0;
            for (int bucket = 0x7bff; bucket >= 0; bucket--)
            {
                int n = _buckets[bucket];
                _buckets[bucket] = active;
                active += n;
            }
            for (int i = 0; i < _depth.Length; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int metric = _depth[i];
                if (metric < 0x7c00) Order[_buckets[metric]++] = (uint)i;
            }
            return active;
        }
    }
}
