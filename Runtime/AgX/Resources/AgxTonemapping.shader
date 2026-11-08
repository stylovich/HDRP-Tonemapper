// HDRP custom post process: AgX tonemapping (SDR, Rec.709 in/out, Rec.2020 inset/outset as in Blender 4.x).
// Matrices come from AgxCore.cs (C#), which also documents the sources. Lives in a Resources folder so it is
// always included in player builds.
Shader "Hidden/AgX/Tonemapping"
{
    Properties
    {
        // This property is necessary to make the CommandBuffer.Blit bind the source texture to _MainTex
        _MainTex("Main Texture", 2DArray) = "grey" {}
    }

    HLSLINCLUDE

    #pragma target 4.5
    #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch switch2

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/PostProcessing/Shaders/RTUpscale.hlsl"

    struct Attributes
    {
        uint vertexID : SV_VertexID;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 texcoord   : TEXCOORD0;
        UNITY_VERTEX_OUTPUT_STEREO
    };

    Varyings Vert(Attributes input)
    {
        Varyings output;
        UNITY_SETUP_INSTANCE_ID(input);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
        output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
        output.texcoord = GetFullScreenTriangleTexCoord(input.vertexID);
        return output;
    }

    TEXTURE2D_X(_MainTex);

    float4x4 _AgxInputMatrix;   // Rec.709 -> Rec.2020 -> AgX inset
    float4x4 _AgxOutsetMatrix;  // AgX outset (encoded space)
    float4x4 _AgxOutputMatrix;  // Rec.2020 -> Rec.709 (linear)
    float4   _AgxParams;        // x: exposure multiplier, y: look enabled (0/1), z: look saturation
    float4   _AgxSlope, _AgxOffset, _AgxPower; // Look (ASC CDL)

    #define AGX_MIN_EV (-12.47393) // log2(2^-10 * 0.18)
    #define AGX_MAX_EV (4.026069)  // log2(2^6.5 * 0.18)

    // AgX base contrast, 6th-order polynomial fit (Benjamin Wrensch, "Minimal AgX implementation").
    float3 AgxSigmoid(float3 x)
    {
        float3 x2 = x * x;
        float3 x4 = x2 * x2;
        return 15.5 * x4 * x2 - 40.14 * x4 * x + 31.96 * x4 - 6.868 * x2 * x + 0.4298 * x2 + 0.1191 * x - 0.00232;
    }

    float3 AgxMap(float3 color)
    {
        color = mul((float3x3)_AgxInputMatrix, max(color, 0.0));

        // Log2 encoding over [AGX_MIN_EV, AGX_MAX_EV].
        color = saturate((log2(max(color, 1e-10)) - AGX_MIN_EV) / (AGX_MAX_EV - AGX_MIN_EV));
        color = AgxSigmoid(color);

        // Look (ASC CDL), then saturation around the luma of the result.
        if (_AgxParams.y > 0.5)
        {
            color = PositivePow(max(color * _AgxSlope.xyz + _AgxOffset.xyz, 0.0), _AgxPower.xyz);
            float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
            color = luma + _AgxParams.z * (color - luma);
        }

        color = mul((float3x3)_AgxOutsetMatrix, color);
        // AgX's encoded output is a 2.2 power curve: back to linear (HDRP's final pass does the sRGB encode).
        color = PositivePow(max(color, 0.0), float3(2.2, 2.2, 2.2));
        return saturate(mul((float3x3)_AgxOutputMatrix, color));
    }

    float4 Frag(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

        float2 uv = ClampAndScaleUVForBilinearPostProcessTexture(input.texcoord.xy);
        float4 source = SAMPLE_TEXTURE2D_X(_MainTex, s_linear_clamp_sampler, uv);
        return float4(AgxMap(source.rgb * _AgxParams.x), source.a);
    }

    // Pass-through (used when HDR output is active, this effect is set up for SDR).
    float4 FragCopy(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        float2 uv = ClampAndScaleUVForBilinearPostProcessTexture(input.texcoord.xy);
        return SAMPLE_TEXTURE2D_X(_MainTex, s_linear_clamp_sampler, uv);
    }

    ENDHLSL

    SubShader
    {
        Tags{ "RenderPipeline" = "HDRenderPipeline" }

        Pass
        {
            Name "AgX Tonemapping"

            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
                #pragma fragment Frag
                #pragma vertex Vert
            ENDHLSL
        }

        Pass
        {
            Name "Copy"

            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
                #pragma fragment FragCopy
                #pragma vertex Vert
            ENDHLSL
        }
    }
    Fallback Off
}
