// HDRP custom post process: AMD FidelityFX LPM tonemapping (SDR, Rec.709 / LPM_CONFIG_709_709).
// Lives in a Resources folder so it is always included in player builds.
Shader "Hidden/FidelityFX/LpmTonemapping"
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
    #include "../Shaders/LpmCore.hlsl"

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

    // LPM control block, built on the CPU by LpmCore.Setup().
    float4 _LpmSaturationContrast;  // xyz: saturation powers, w: contrast power
    float4 _LpmToneScaleBias;       // xy: toneScaleBias, z: shoulder contrast, w: shoulder enabled (0/1)
    float4 _LpmLumaT;               // xyz: luma coefficients for crosstalk mapping
    float4 _LpmRcpLumaT;            // xyz: 1 / lumaT
    float4 _LpmCrosstalk;           // xyz: crosstalk
    float4 _LpmInput;               // x: pre-exposure (linear multiplier), y: hdrMax (input clamp)
    float4 _LpmHighlightDesat;      // x: highlight desaturation strength, y: start (mapped luma). Extension, not in AMD's LPM.

    float4 Frag(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

        float2 uv = ClampAndScaleUVForBilinearPostProcessTexture(input.texcoord.xy);
        float4 source = SAMPLE_TEXTURE2D_X(_MainTex, s_linear_clamp_sampler, uv);

        // LPM expects linear input in {0 to hdrMax}.
        float3 color = clamp(source.rgb * _LpmInput.x, 0.0, _LpmInput.y);

        // LPM_CONFIG_709_709: no gamut conversion, SDR output.
        LpmMap(color,
            _LpmLumaT.xyz,                  // lumaW (unused when soft = false)
            _LpmLumaT.xyz,
            _LpmRcpLumaT.xyz,
            _LpmSaturationContrast.xyz,
            _LpmSaturationContrast.w,
            _LpmToneScaleBias.w > 0.5,
            _LpmToneScaleBias.z,
            _LpmToneScaleBias.xy,
            _LpmCrosstalk.xyz,
            false, float3(0, 0, 0), float3(0, 0, 0), float3(0, 0, 0), // con
            false, float2(0, 0),                                       // soft
            false, false, false,                                       // con2, clip, scaleOnly
            float3(0, 0, 0), float3(0, 0, 0), float3(0, 0, 0),
            _LpmHighlightDesat.xy);

        // Output stays linear: HDRP's final pass does the sRGB encode.
        return float4(color, source.a);
    }

    // Pass-through (used when HDR output is active, LPM is set up for SDR here).
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
            Name "LPM Tonemapping"

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
