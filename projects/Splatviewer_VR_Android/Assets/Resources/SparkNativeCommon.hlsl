// SPDX-License-Identifier: MIT
// Shared packed Gaussian projection for color and XR motion/depth.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<uint4> _SparkPacked;
            StructuredBuffer<uint> _SparkOrder;
            float4x4 _SparkLocalToWorld;
            float4 _SparkRenderSize;
            #if defined(SPARK_MOTION)
            float4x4 _SparkPreviousVP[2];
            float4x4 _SparkMotionView[2];
            float4x4 _SparkMotionProjection[2];
            float4x4 _SparkMotionInverseVP[2];
            float4x4 _SparkPreviousFromCurrent;
            float _SparkMotionY;
            #endif
            static const float MaxStdDev = 2.2360679775;
            static const float BlurAmount = 0.3;
            static const float MinAlpha = 1.0 / 255.0;

            float4 DecodeQuaternion(uint encoded)
            {
                float2 oct = float2(encoded & 255u, (encoded >> 8) & 255u) * (2.0 / 255.0) - 1.0;
                float3 axis = float3(oct, 1.0 - abs(oct.x) - abs(oct.y));
                float t = max(-axis.z, 0.0);
                axis.xy += float2(axis.x >= 0 ? -t : t, axis.y >= 0 ? -t : t);
                axis = normalize(axis);
                float s, c;
                sincos(float(encoded >> 16) * (3.141592653589793 / 510.0), s, c);
                return float4(axis * s, c);
            }

            void Decode(uint4 p, out float3 center, out float3 scale, out float4 q, out float4 color)
            {
                center = float3(f16tof32(p.y & 65535u), f16tof32(p.y >> 16), f16tof32(p.z & 65535u));
                color = float4(p.x & 255u, (p.x >> 8) & 255u, (p.x >> 16) & 255u, p.x >> 24) / 255.0;
                uint3 u = uint3(p.w & 255u, (p.w >> 8) & 255u, (p.w >> 16) & 255u);
                scale = exp(-12.0 + (float3(u) - 1.0) * (21.0 / 254.0));
                scale *= float3(u.x != 0, u.y != 0, u.z != 0);
                q = DecodeQuaternion((p.z >> 16) | ((p.w >> 8) & 0xff0000u));
            }

            float3x3 RotationScale(float4 q, float3 scale)
            {
                float x=q.x, y=q.y, z=q.z, w=q.w;
                float3x3 rotation = float3x3(
                    1-2*(y*y+z*z), 2*(x*y-w*z), 2*(x*z+w*y),
                    2*(x*y+w*z), 1-2*(x*x+z*z), 2*(y*z-w*x),
                    2*(x*z-w*y), 2*(y*z+w*x), 1-2*(x*x+y*y));
                return mul(rotation, float3x3(scale.x,0,0, 0,scale.y,0, 0,0,scale.z));
            }

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                uint instanceID : SV_InstanceID;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 splatUV : TEXCOORD0;
                half4 color : COLOR0;
                #if defined(SPARK_MOTION)
                float4 currentClip : TEXCOORD1;
                float4 previousClip : TEXCOORD2;
                #endif
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output = (Varyings)0;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = float4(0,0,-2,1);
                uint instance = input.instanceID;
                #if UNITY_ANY_INSTANCING_ENABLED
                    instance = unity_InstanceID;
                #endif
                uint index = _SparkOrder[instance];
                uint4 packed = _SparkPacked[index];
                if ((packed.x >> 24) == 0) return output;
                float3 center, scale;
                float4 q, color;
                Decode(packed, center, scale, q, color);
                float3 worldCenter = mul(_SparkLocalToWorld, float4(center,1)).xyz;
                #if defined(SPARK_MOTION)
                uint eye = 0;
                #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                    eye = unity_StereoEyeIndex;
                #endif
                // XR motion/depth uses its own unflipped projection, as URP does.
                // Color-target builtins may be flipped and must not enter this path.
                float4x4 viewMatrix = _SparkMotionView[eye];
                float4x4 projectionMatrix = _SparkMotionProjection[eye];
                float3 viewCenter = mul(viewMatrix, float4(worldCenter,1)).xyz;
                float4 clipCenter = mul(projectionMatrix, float4(viewCenter,1));
                #else
                float4x4 viewMatrix = UNITY_MATRIX_V;
                float4x4 projectionMatrix = UNITY_MATRIX_P;
                float3 viewCenter = TransformWorldToView(worldCenter);
                float4 clipCenter = TransformWorldToHClip(worldCenter);
                #endif
                if (viewCenter.z >= -_ProjectionParams.y || viewCenter.z <= -_ProjectionParams.z ||
                    abs(clipCenter.x) > 1.4 * clipCenter.w || abs(clipCenter.y) > 1.4 * clipCenter.w)
                    return output;

                // General basis also handles the OpenCV Y reflection.
                float3x3 viewBasis = mul((float3x3)viewMatrix, (float3x3)_SparkLocalToWorld);
                float3x3 RS = mul(viewBasis, RotationScale(q, scale));
                float3x3 cov3D = mul(RS, transpose(RS));
                float2 focal = 0.5 * _SparkRenderSize.xy * float2(projectionMatrix._m00, projectionMatrix._m11);
                float invZ = rcp(viewCenter.z);
                float2 J1 = focal * invZ;
                float2 J2 = -(J1 * viewCenter.xy) * invZ;
                float3x3 J = float3x3(J1.x,0,J2.x, 0,J1.y,J2.y, 0,0,0);
                float3x3 covariance = mul(J, mul(cov3D, transpose(J)));
                float a=covariance._m00, b=covariance._m01, d=covariance._m11;
                float determinantOriginal = a*d-b*b;
                a += BlurAmount;
                d += BlurAmount;
                float determinant = a*d-b*b;
                color.a *= sqrt(max(0.0, determinantOriginal / max(determinant, 1e-20)));
                if (color.a < MinAlpha) return output;

                float average = 0.5*(a+d);
                float delta = sqrt(max(0.0, average*average-determinant));
                float lambda1 = max(0.0, average+delta), lambda2 = max(0.0, average-delta);
                float2 axis1 = abs(b)>0.001 ? normalize(float2(b,lambda1-a)) :
                    (a>=d ? float2(1,0) : float2(0,1));
                float2 axis2 = float2(axis1.y,-axis1.x);
                float2 radius = min(512.0, MaxStdDev * sqrt(float2(lambda1,lambda2)));
                static const float2 corners[4] = {
                    float2(-1,-1), float2(1,-1), float2(1,1),
                    float2(-1,1)
                };
                float2 corner = corners[input.vertexID];
                float2 offset = corner.x*axis1*radius.x + corner.y*axis2*radius.y;
                output.positionCS = clipCenter;
                output.positionCS.xy += (2.0 / _SparkRenderSize.xy) * offset * clipCenter.w;
                #if defined(SPARK_MOTION)
                output.currentClip = output.positionCS;
                float4 worldCorner = mul(_SparkMotionInverseVP[eye], output.positionCS);
                worldCorner /= worldCorner.w;
                float4 previousWorld = mul(_SparkPreviousFromCurrent, worldCorner);
                output.previousClip = mul(_SparkPreviousVP[eye], previousWorld);
                #endif
                output.splatUV = corner*MaxStdDev;
                output.color = color;
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float squared = dot(input.splatUV,input.splatUV);
                clip(MaxStdDev*MaxStdDev-squared);
                half alpha = input.color.a * exp(-0.5*squared);
                #if defined(SPARK_MOTION)
                // One depth per pixel: use the nearest sufficiently opaque splat.
                // This is intentionally experimental at transparent edges.
                clip(alpha - 0.2);
                float3 motion = input.currentClip.xyz / input.currentClip.w -
                    input.previousClip.xyz / input.previousClip.w;
                motion.y *= _SparkMotionY;
                return float4(motion, 1);
                #else
                clip(alpha-MinAlpha);
                half3 rgb = input.color.rgb;
                #ifndef UNITY_COLORSPACE_GAMMA
                    rgb = pow(max(rgb, 0), 2.2);
                #endif
                return half4(rgb*alpha,alpha);
                #endif
            }
