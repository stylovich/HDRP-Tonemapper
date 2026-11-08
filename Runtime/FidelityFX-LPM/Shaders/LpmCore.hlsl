// HLSL port of AMD FidelityFX LPM (Luma Preserving Mapper) 1.20200225 - GPU mapper (32-bit path).
// Original: https://github.com/GPUOpen-Effects/FidelityFX-LPM (ffx_lpm.h)
// Copyright (c) 2017-2019 Advanced Micro Devices, Inc. All rights reserved. MIT License (see "Third Party Notices.md" at the package root).
//
// Differences from the original:
//  - Constants come in as plain floats (built by LpmCore.Setup() in C#) instead of the packed uint4[24] control block.
//  - Two divisions are guarded so pure black and fully clipped white can't produce NaN (0 * inf in the original).
//  - pow() uses Unity's SafePositivePow() to avoid pow(0, 0) = NaN (saturation powers can reach 0).
//  - Extension, not in AMD's LPM: optional highlight desaturation (last parameter). With strength 0 the
//    mapping is the original one.
// Requires "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl" to be included first.

#ifndef FFX_LPM_CORE_HLSL
#define FFX_LPM_CORE_HLSL

#define LPM_EPSILON 1e-10

float LpmMax3(float3 c) { return max(c.r, max(c.g, c.b)); }

// LpmMap() from ffx_lpm.h. The bool path controls (shoulder, con, soft, con2, clip, scaleOnly) should be
// compile-time constants (or uniforms) so the branches fold away.
void LpmMap(inout float3 color,  // Input and output color.
    float3 lumaW,                // Luma coef for RGB working space.
    float3 lumaT,                // Luma coef for crosstalk mapping (working or output color-space depending on usage case).
    float3 rcpLumaT,             // 1/lumaT.
    float3 saturation,           // Saturation powers.
    float  contrast,             // Contrast power.
    bool   shoulder,             // Using shoulder tuning.
    float  shoulderContrast,     // Shoulder power.
    float2 toneScaleBias,        // Other tonemapping parameters.
    float3 crosstalk,            // Crosstalk scaling for over-exposure color shaping.
    bool   con,                  // Use first RGB conversion matrix, if 'soft' then 'con' must be true also.
    float3 conR, float3 conG, float3 conB, // Working to output space conversion.
    bool   soft,                 // Use soft gamut mapping.
    float2 softGap,              // {x,(1-x)/(x*0.693147180559)}, where 'x' is gamut mapping soft fall-off amount.
    bool   con2,                 // Use last RGB conversion matrix.
    bool   clip,                 // Use clipping on last conversion matrix.
    bool   scaleOnly,            // Do scaling only (special case for 709 HDR to scRGB).
    float3 con2R, float3 con2G, float3 con2B, // Secondary RGB conversion matrix.
    float2 highlightDesat)       // Extension: x = strength {0 to 1}, y = mapped luma where it starts (< 1).
{
    // Grab original RGB ratio.
    float3 ratio = color * rcp(max(LpmMax3(color), LPM_EPSILON));
    // Apply saturation, ratio must be max 1.0 for this to work right.
    ratio = SafePositivePow(ratio, saturation);

    // Tonemap luma, note this uses the original color, so saturation is luma preserving.
    float luma = soft ? dot(color, lumaW) : dot(color, lumaT);
    luma = SafePositivePow(luma, contrast);
    float lumaShoulder = shoulder ? SafePositivePow(luma, shoulderContrast) : luma;
    luma = luma * rcp(lumaShoulder * toneScaleBias.x + toneScaleBias.y);

    if (soft)
    {
        if (con)
        {
            // Converting ratio instead of color. Change of primaries.
            float3 c = ratio;
            ratio = float3(dot(c, conR), dot(c, conG), dot(c, conB));
            // Convert ratio to max 1 again.
            ratio *= rcp(max(LpmMax3(ratio), LPM_EPSILON));
        }
        // Absolute gamut mapping converted to soft falloff (maintains max 1 property).
        ratio = min(max(softGap.x, saturate(ratio * -softGap.x + ratio)), saturate(softGap.x * exp2(ratio * softGap.y)));
    }

    // Extension (not in AMD's LPM): highlight desaturation. Pulls the ratio towards white as the mapped luma
    // goes from highlightDesat.y to 1, like the path to white of per-channel tonemappers. The ratio keeps its
    // max at 1 and the scaling below still hits the target luma, so it stays luma preserving.
    if (highlightDesat.x > 0.0)
        ratio = lerp(ratio, 1.0, highlightDesat.x * smoothstep(highlightDesat.y, 1.0, luma));

    // Compute ratio scaler required to hit target luma (limited to not clip).
    float lumaRatio = dot(ratio, lumaT);
    float ratioScale = saturate(luma * rcp(lumaRatio));
    // Assume in gamut, compute output color.
    color = saturate(ratio * ratioScale);
    // Capability per channel to increase value, factoring in crosstalk: (1.0-ratio)*crosstalk.
    float3 cap = -crosstalk * color + crosstalk;
    // Amount of luma needed to add to non-clipped channels to make up for clipping.
    float lumaAdd = saturate(luma - dot(color, lumaT));
    // Amount to increase keeping over-exposure ratios constant and possibly exceeding clipping point.
    float t = lumaAdd * rcp(max(dot(cap, lumaT), LPM_EPSILON));
    // Add amounts to base color but clip.
    color = saturate(t * cap + color);
    // Compute amount of luma needed to add to non-clipped channel to make up for clipping, add to last channel.
    lumaAdd = saturate(luma - dot(color, lumaT));
    color = saturate(lumaAdd * rcpLumaT + color);

    // Last optional place to convert from smaller to larger gamut (or do clipped conversion).
    if (con2)
    {
        float3 c = color;
        color = float3(dot(c, con2R), dot(c, con2G), dot(c, con2B));
        if (clip)
            color = saturate(color);
    }

    if (scaleOnly)
        color *= con2R.x;
}

#endif // FFX_LPM_CORE_HLSL
