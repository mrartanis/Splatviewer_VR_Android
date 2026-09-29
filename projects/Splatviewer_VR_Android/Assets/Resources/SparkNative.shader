// SPDX-License-Identifier: MIT
// Port of World Labs Spark PackedSplats decoding and splatVertex/splatFragment.
// See third_party/spark/LICENSE and README.md.
Shader "VRPhoto/Spark Native Splats"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Pass
        {
            Name "SparkDirect"
            ZWrite Off
            ZTest LEqual
            Blend One OneMinusSrcAlpha
            Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "SparkNativeCommon.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "SparkMotionDepth"
            Tags { "LightMode"="XRMotionVectors" }
            ZWrite On
            ZTest LEqual
            Blend Off
            Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #define SPARK_MOTION 1
            #include "SparkNativeCommon.hlsl"
            ENDHLSL
        }
    }
}
