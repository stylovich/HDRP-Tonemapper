// C# port of AMD FidelityFX LPM (Luma Preserving Mapper) 1.20200225 - setup + CPU reference mapper.
// Original: https://github.com/GPUOpen-Effects/FidelityFX-LPM (ffx_lpm.h)
// Copyright (c) 2017-2019 Advanced Micro Devices, Inc. All rights reserved. MIT License (see "Third Party Notices.md" at the package root).
//
// LpmSetup() is evaluated on the CPU (double precision) and uploaded as plain float constants.
// LpmMap() mirrors the GPU code in Shaders/LpmCore.hlsl and is used by the inspector previews.
// Extension, not in AMD's LPM: optional highlight desaturation (LpmConstants.highlightDesaturation, off when 0).

using UnityEngine;

namespace FidelityFX.Lpm
{
    /// <summary>xy chromaticity coordinates of a color space (primaries + white point).</summary>
    public readonly struct LpmColorSpace
    {
        public readonly Vector2 r, g, b, w;
        public LpmColorSpace(Vector2 r, Vector2 g, Vector2 b, Vector2 w) { this.r = r; this.g = g; this.b = b; this.w = w; }

        public static readonly Vector2 D65 = new Vector2(0.3127f, 0.3290f);
        public static readonly LpmColorSpace Rec709 = new LpmColorSpace(new Vector2(0.64f, 0.33f), new Vector2(0.30f, 0.60f), new Vector2(0.15f, 0.06f), D65);
        public static readonly LpmColorSpace P3D65 = new LpmColorSpace(new Vector2(0.680f, 0.320f), new Vector2(0.265f, 0.690f), new Vector2(0.150f, 0.060f), D65);
        public static readonly LpmColorSpace Rec2020 = new LpmColorSpace(new Vector2(0.708f, 0.292f), new Vector2(0.170f, 0.797f), new Vector2(0.131f, 0.046f), D65);
    }

    /// <summary>Path control, equivalent to the LPM_CONFIG_* prefabs (CON, SOFT, CON2, CLIP, SCALEONLY).</summary>
    public readonly struct LpmConfig
    {
        public readonly bool con, soft, con2, clip, scaleOnly;
        public LpmConfig(bool con, bool soft, bool con2, bool clip, bool scaleOnly)
        { this.con = con; this.soft = soft; this.con2 = con2; this.clip = clip; this.scaleOnly = scaleOnly; }

        /// <summary>LPM_CONFIG_709_709: Rec.709 working space to SDR Rec.709/sRGB output (what HDRP uses for SDR).</summary>
        public static readonly LpmConfig Sdr709 = new LpmConfig(false, false, false, false, false);
    }

    /// <summary>The LPM control block (unpacked, 32-bit part only).</summary>
    public struct LpmConstants
    {
        public Vector3 saturation;      // Per-channel saturation powers (already contrast-relative).
        public float contrast;          // Contrast power (1 = no extra contrast).
        public Vector2 toneScaleBias;
        public Vector3 lumaT, rcpLumaT; // Luma coefficients used for crosstalk mapping.
        public Vector3 crosstalk;
        public Vector3 lumaW;           // Luma coefficients of the working space.
        public float shoulderContrast;
        public bool shoulder;           // Use the shoulder path (shoulderContrast != 1).
        public Vector2 softGap;
        public Vector3 conR, conG, conB;    // Working -> output matrix (soft gamut mapping).
        public Vector3 con2R, con2G, con2B; // Output -> container matrix.
        public LpmConfig config;

        /// <summary>
        /// Extension, not part of AMD's LPM (Setup() leaves it at zero = off). x: strength {0 to 1},
        /// y: mapped (output, linear) luma where the desaturation towards white starts; it reaches full strength at 1.
        /// </summary>
        public Vector2 highlightDesaturation;
    }

    public static class LpmCore
    {
        /// <summary>Most-saturated crosstalk value allowed (crosstalk channels must be above zero).</summary>
        public const float MinCrosstalk = 1f / 1024f;

        /// <summary>
        /// Builds the LPM control block. Mirrors LpmSetup() from ffx_lpm.h.
        /// </summary>
        /// <param name="hdrMax">Maximum input value.</param>
        /// <param name="exposure">Number of stops between hdrMax and the 18% mid-level on input.</param>
        /// <param name="contrast">0 = no extra contrast, 1 = maximum contrast.</param>
        /// <param name="shoulderContrast">Shoulder shaping, 1 = no change.</param>
        /// <param name="saturation">Per-channel adjustment: &lt;0 decrease, 0 no change, &gt;0 increase.</param>
        /// <param name="crosstalk">Over-exposure color shaping. One channel should be 1, the rest &lt;= 1 but not zero.</param>
        public static LpmConstants Setup(
            LpmConfig config,
            in LpmColorSpace working, in LpmColorSpace output, in LpmColorSpace container, float scaleC,
            float softGap, float hdrMax, float exposure, float contrast, float shoulderContrast,
            Vector3 saturation, Vector3 crosstalk)
        {
            var k = new LpmConstants { config = config };

            // Contrast needs to be 1.0 based for no contrast; saturation is based on contrast.
            double con = contrast + 1.0;
            // A negative power would push ratios above 1 and break the mapper, so clamp at 0 (= full desaturation).
            k.saturation = new Vector3(
                Mathf.Max(0f, (float)(saturation.x + con)),
                Mathf.Max(0f, (float)(saturation.y + con)),
                Mathf.Max(0f, (float)(saturation.z + con)));
            k.contrast = (float)con;

            double gap = System.Math.Max(softGap, 1.0 / 1024.0);

            double midIn = hdrMax * 0.18 * System.Math.Pow(2.0, -exposure);
            const double midOut = 0.18;

            double cs = con * shoulderContrast;
            double z0 = -System.Math.Pow(midIn, con);
            double z1 = System.Math.Pow(hdrMax, cs) * System.Math.Pow(midIn, con);
            double z2 = System.Math.Pow(hdrMax, con) * System.Math.Pow(midIn, cs) * midOut;
            double z3 = System.Math.Pow(hdrMax, cs) * midOut;
            double z4 = System.Math.Pow(midIn, cs) * midOut;
            double tsbX = -((z0 + (midOut * (z1 - z2)) / (z3 - z4)) / z4);

            double w0 = System.Math.Pow(hdrMax, cs) * System.Math.Pow(midIn, con);
            double w1 = System.Math.Pow(hdrMax, con) * System.Math.Pow(midIn, cs) * midOut;
            double w2 = System.Math.Pow(hdrMax, cs) * midOut;
            double w3 = System.Math.Pow(midIn, cs) * midOut;
            double tsbY = (w0 - w1) / (w2 - w3);
            k.toneScaleBias = new Vector2((float)tsbX, (float)tsbY);

            // Working space luma = Y row of RGB->XYZ, normalized to sum to 1.
            var rgbToXyzW = RgbToXyz(working);
            k.lumaW = ToVector(Normalize(rgbToXyzW.Row(1)));

            // Crosstalk luma is based on the output space when soft gamut mapping is used, else the working space.
            var rgbToXyzO = RgbToXyz(output);
            var lumaT = Normalize(config.soft ? rgbToXyzO.Row(1) : rgbToXyzW.Row(1));
            k.lumaT = ToVector(lumaT);
            k.rcpLumaT = new Vector3((float)(1.0 / lumaT.x), (float)(1.0 / lumaT.y), (float)(1.0 / lumaT.z));

            k.softGap = config.soft ? new Vector2((float)gap, (float)((1.0 - gap) / (gap * 0.693147180559))) : Vector2.zero;

            // First conversion is always working to output.
            if (config.con)
            {
                var m = rgbToXyzO.Inverse() * rgbToXyzW;
                k.conR = ToVector(m.Row(0)); k.conG = ToVector(m.Row(1)); k.conB = ToVector(m.Row(2));
            }

            // The last conversion is always output to container.
            if (config.con2)
            {
                var m = RgbToXyz(container).Inverse() * rgbToXyzO;
                k.con2R = ToVector(m.Row(0)) * scaleC; k.con2G = ToVector(m.Row(1)) * scaleC; k.con2B = ToVector(m.Row(2)) * scaleC;
            }
            if (config.scaleOnly) k.con2R.x = scaleC;

            k.shoulderContrast = shoulderContrast;
            k.shoulder = !Mathf.Approximately(shoulderContrast, 1f);
            k.crosstalk = new Vector3(
                Mathf.Clamp(crosstalk.x, MinCrosstalk, 1f),
                Mathf.Clamp(crosstalk.y, MinCrosstalk, 1f),
                Mathf.Clamp(crosstalk.z, MinCrosstalk, 1f));
            return k;
        }

        /// <summary>LpmSetup() with LPM_CONFIG_709_709 / LPM_COLORS_709_709 (SDR output, Rec.709 working space).</summary>
        public static LpmConstants Setup709(float hdrMax, float exposure, float contrast, float shoulderContrast, Vector3 saturation, Vector3 crosstalk)
        {
            return Setup(LpmConfig.Sdr709, LpmColorSpace.Rec709, LpmColorSpace.Rec709, LpmColorSpace.Rec709, 1f,
                0f, hdrMax, exposure, contrast, shoulderContrast, saturation, crosstalk);
        }

        /// <summary>
        /// CPU reference of LpmMap() (32-bit path). Input is linear {0..hdrMax}, output linear {0..1} for SDR configs.
        /// Kept in sync with LpmMap() in Shaders/LpmCore.hlsl.
        /// </summary>
        public static Vector3 Map(Vector3 color, in LpmConstants k)
        {
            float r = Mathf.Max(color.x, 0f), g = Mathf.Max(color.y, 0f), b = Mathf.Max(color.z, 0f);

            // Grab original RGB ratio (guarded against black, which would be 0 * inf in the original).
            float rcpMax = 1f / Mathf.Max(Mathf.Max(r, Mathf.Max(g, b)), 1e-10f);
            float ratioR = r * rcpMax, ratioG = g * rcpMax, ratioB = b * rcpMax;
            // Apply saturation, ratio must be max 1.0 for this to work right.
            ratioR = PositivePow(ratioR, k.saturation.x);
            ratioG = PositivePow(ratioG, k.saturation.y);
            ratioB = PositivePow(ratioB, k.saturation.z);

            // Tonemap luma (uses the original color, so saturation is luma preserving).
            Vector3 lumaCoef = k.config.soft ? k.lumaW : k.lumaT;
            float luma = g * lumaCoef.y + (r * lumaCoef.x + (b * lumaCoef.z));
            luma = PositivePow(luma, k.contrast);
            float lumaShoulder = k.shoulder ? PositivePow(luma, k.shoulderContrast) : luma;
            luma = luma / (lumaShoulder * k.toneScaleBias.x + k.toneScaleBias.y);

            if (k.config.soft)
            {
                if (k.config.con)
                {
                    float cr = ratioR, cg = ratioG, cb = ratioB;
                    ratioR = cr * k.conR.x + (cg * k.conR.y + (cb * k.conR.z));
                    ratioG = cg * k.conG.y + (cr * k.conG.x + (cb * k.conG.z));
                    ratioB = cb * k.conB.z + (cg * k.conB.y + (cr * k.conB.x));
                    rcpMax = 1f / Mathf.Max(Mathf.Max(ratioR, Mathf.Max(ratioG, ratioB)), 1e-10f);
                    ratioR *= rcpMax; ratioG *= rcpMax; ratioB *= rcpMax;
                }
                ratioR = SoftFalloff(ratioR, k.softGap);
                ratioG = SoftFalloff(ratioG, k.softGap);
                ratioB = SoftFalloff(ratioB, k.softGap);
            }

            // Extension (not in AMD's LPM): pull the ratio towards white as the mapped luma approaches 1.
            if (k.highlightDesaturation.x > 0f)
            {
                float w = k.highlightDesaturation.x * SmoothStep(k.highlightDesaturation.y, 1f, luma);
                ratioR += (1f - ratioR) * w;
                ratioG += (1f - ratioG) * w;
                ratioB += (1f - ratioB) * w;
            }

            Vector3 lt = k.lumaT;
            // Compute ratio scaler required to hit target luma, limited to not clip.
            float lumaRatio = ratioR * lt.x + ratioG * lt.y + ratioB * lt.z;
            float ratioScale = Sat(luma / lumaRatio);
            // Assume in gamut, compute output color.
            float oR = Sat(ratioR * ratioScale), oG = Sat(ratioG * ratioScale), oB = Sat(ratioB * ratioScale);
            // Capability per channel to increase value, factoring in crosstalk.
            float capR = -k.crosstalk.x * oR + k.crosstalk.x;
            float capG = -k.crosstalk.y * oG + k.crosstalk.y;
            float capB = -k.crosstalk.z * oB + k.crosstalk.z;
            // Luma needed to add to non-clipped channels to make up for clipping.
            float lumaAdd = Sat((-oB) * lt.z + ((-oR) * lt.x + ((-oG) * lt.y + luma)));
            // Guarded: when every channel is clipped the original computes 0 * inf.
            float t = lumaAdd / Mathf.Max(capG * lt.y + (capR * lt.x + (capB * lt.z)), 1e-10f);
            oR = Sat(t * capR + oR); oG = Sat(t * capG + oG); oB = Sat(t * capB + oB);
            // Add what is still missing to the last channel.
            lumaAdd = Sat((-oB) * lt.z + ((-oR) * lt.x + ((-oG) * lt.y + luma)));
            oR = Sat(lumaAdd * k.rcpLumaT.x + oR);
            oG = Sat(lumaAdd * k.rcpLumaT.y + oG);
            oB = Sat(lumaAdd * k.rcpLumaT.z + oB);

            if (k.config.con2)
            {
                float cr = oR, cg = oG, cb = oB;
                oR = cr * k.con2R.x + (cg * k.con2R.y + (cb * k.con2R.z));
                oG = cg * k.con2G.y + (cr * k.con2G.x + (cb * k.con2G.z));
                oB = cb * k.con2B.z + (cg * k.con2B.y + (cr * k.con2B.x));
                if (k.config.clip) { oR = Sat(oR); oG = Sat(oG); oB = Sat(oB); }
            }
            if (k.config.scaleOnly) { oR *= k.con2R.x; oG *= k.con2R.x; oB *= k.con2R.x; }

            return new Vector3(oR, oG, oB);
        }

        /// <summary>Input (linear) mid-gray that LPM maps to 0.18 for the given hdrMax / exposure.</summary>
        public static float MidGrayInput(float hdrMax, float exposure) => hdrMax * 0.18f * Mathf.Pow(2f, -exposure);

        // ---------------------------------------------------------------------------------------------------------------
        // Helpers

        static float Sat(float x) => float.IsNaN(x) ? 0f : Mathf.Clamp01(x); // HLSL saturate(NaN) == 0
        static float PositivePow(float x, float p) => Mathf.Pow(Mathf.Max(Mathf.Abs(x), 5.960464478e-8f), p); // Same as SafePositivePow() in Common.hlsl

        // HLSL smoothstep() (Mathf.SmoothStep interpolates between two values instead).
        static float SmoothStep(float a, float b, float x)
        {
            float t = Sat((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        static float SoftFalloff(float ratio, Vector2 gap)
        {
            return Mathf.Min(Mathf.Max(gap.x, Sat(ratio * -gap.x + ratio)), Sat(gap.x * Mathf.Pow(2f, ratio * gap.y)));
        }

        struct D3
        {
            public double x, y, z;
            public D3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
            public static double Dot(D3 a, D3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        }

        static D3 Normalize(D3 v) { double s = 1.0 / (v.x + v.y + v.z); return new D3(v.x * s, v.y * s, v.z * s); }
        static Vector3 ToVector(D3 v) => new Vector3((float)v.x, (float)v.y, (float)v.z);

        /// <summary>Row-major 3x3 double matrix.</summary>
        struct M3
        {
            public D3 r0, r1, r2;
            public M3(D3 r0, D3 r1, D3 r2) { this.r0 = r0; this.r1 = r1; this.r2 = r2; }
            public D3 Row(int i) => i == 0 ? r0 : (i == 1 ? r1 : r2);
            public D3 Col(int i) => i == 0 ? new D3(r0.x, r1.x, r2.x) : (i == 1 ? new D3(r0.y, r1.y, r2.y) : new D3(r0.z, r1.z, r2.z));

            public static M3 operator *(M3 a, M3 b)
            {
                D3 c0 = b.Col(0), c1 = b.Col(1), c2 = b.Col(2);
                return new M3(
                    new D3(D3.Dot(a.r0, c0), D3.Dot(a.r0, c1), D3.Dot(a.r0, c2)),
                    new D3(D3.Dot(a.r1, c0), D3.Dot(a.r1, c1), D3.Dot(a.r1, c2)),
                    new D3(D3.Dot(a.r2, c0), D3.Dot(a.r2, c1), D3.Dot(a.r2, c2)));
            }

            // Same closed form as LpmMatInv3x3().
            public M3 Inverse()
            {
                D3 ix = r0, iy = r1, iz = r2;
                double i = 1.0 / (ix.x * (iy.y * iz.z - iz.y * iy.z) - ix.y * (iy.x * iz.z - iy.z * iz.x) + ix.z * (iy.x * iz.y - iy.y * iz.x));
                return new M3(
                    new D3((iy.y * iz.z - iz.y * iy.z) * i, (ix.z * iz.y - ix.y * iz.z) * i, (ix.y * iy.z - ix.z * iy.y) * i),
                    new D3((iy.z * iz.x - iy.x * iz.z) * i, (ix.x * iz.z - ix.z * iz.x) * i, (iy.x * ix.z - ix.x * iy.z) * i),
                    new D3((iy.x * iz.y - iz.x * iy.y) * i, (iz.x * ix.y - ix.x * iz.y) * i, (ix.x * iy.y - iy.x * ix.y) * i));
            }
        }

        // LpmColRgbToXyz(): RGB -> XYZ matrix from xy chromaticities (rows are X, Y, Z).
        static M3 RgbToXyz(in LpmColorSpace cs)
        {
            D3 rz = XyToXyz(cs.r), gz = XyToXyz(cs.g), bz = XyToXyz(cs.b);
            var prim = new M3(new D3(rz.x, gz.x, bz.x), new D3(rz.y, gz.y, bz.y), new D3(rz.z, gz.z, bz.z)); // Transposed.
            D3 w = XyToXyz(cs.w);
            double wy = 1.0 / cs.w.y;
            var w3 = new D3(w.x * wy, w.y * wy, w.z * wy);
            var inv = prim.Inverse();
            var s = new D3(D3.Dot(inv.r0, w3), D3.Dot(inv.r1, w3), D3.Dot(inv.r2, w3));
            return new M3(
                new D3(prim.r0.x * s.x, prim.r0.y * s.y, prim.r0.z * s.z),
                new D3(prim.r1.x * s.x, prim.r1.y * s.y, prim.r1.z * s.z),
                new D3(prim.r2.x * s.x, prim.r2.y * s.y, prim.r2.z * s.z));
        }

        static D3 XyToXyz(Vector2 xy) => new D3(xy.x, xy.y, 1.0 - (xy.x + xy.y));
    }
}
