// AgX display transform (Troy Sobotka) - Rec.2020 variant, as used by Blender 4.x.
// Same formulation as Google Filament (filament/src/ToneMapper.cpp, Apache-2.0) and three.js (AgXToneMapping, MIT):
//  - Inset / outset matrices: Blender's AgX (EaryChow/AgX_LUT_Gen, AgXBaseRec2020.py), as published in Filament.
//  - Sigmoid: 6th-order polynomial fit from Benjamin Wrensch's "Minimal AgX implementation" (iolite-engine.com), as in three.js.
//  - Looks (ASC CDL): Golden / Punchy from the same "Minimal AgX implementation" (MIT), with the luma for the
//    saturation computed after the CDL, as in its 2025 revision (Filament uses Golden saturation 1.3 and the luma
//    before the CDL, which turns grays strongly yellow).
// See "Third Party Notices.md" at the package root.
//
// This file is the CPU reference (inspector previews, tests) and builds the matrices uploaded to the shader.

using UnityEngine;

namespace AgX
{
    public enum AgxLook
    {
        None = 0,
        Punchy = 1,
        Golden = 2,
        Custom = 3,
    }

    /// <summary>ASC CDL applied in AgX's encoded space, between the sigmoid and the outset matrix.</summary>
    public readonly struct AgxCdl
    {
        public readonly Vector3 slope, offset, power;
        public readonly float saturation;

        public AgxCdl(Vector3 slope, Vector3 offset, Vector3 power, float saturation)
        { this.slope = slope; this.offset = offset; this.power = power; this.saturation = saturation; }

        public static readonly AgxCdl Identity = new AgxCdl(Vector3.one, Vector3.zero, Vector3.one, 1f);
        public static readonly AgxCdl Punchy = new AgxCdl(Vector3.one, Vector3.zero, Vector3.one * 1.35f, 1.4f);
        public static readonly AgxCdl Golden = new AgxCdl(new Vector3(1f, 0.9f, 0.5f), Vector3.zero, Vector3.one * 0.8f, 0.8f);

        public bool IsIdentity => slope == Vector3.one && offset == Vector3.zero && power == Vector3.one && Mathf.Approximately(saturation, 1f);
    }

    public static class AgxCore
    {
        /// <summary>log2(2^-10 * 0.18): bottom of the encoding range (10 stops below mid-gray).</summary>
        public const float MinEv = -12.47393f;
        /// <summary>log2(2^6.5 * 0.18): top of the encoding range (6.5 stops above mid-gray, ~16.3 linear).</summary>
        public const float MaxEv = 4.026069f;

        /// <summary>Linear Rec.709 -> Rec.2020, then AgX inset. Row-major (M * v), 3x3 in the upper-left.</summary>
        public static readonly Matrix4x4 InputMatrix;
        /// <summary>AgX outset (inverse of Blender's outset-inverse matrix), applied in encoded space.</summary>
        public static readonly Matrix4x4 OutsetMatrix;
        /// <summary>Linear Rec.2020 -> Rec.709.</summary>
        public static readonly Matrix4x4 OutputMatrix;

        static readonly Vector3 k_LookLuma = new Vector3(0.2126f, 0.7152f, 0.0722f);

        static AgxCore()
        {
            // Blender's matrices, written as rows (Filament / three.js list them column by column).
            var inset = new D33(
                0.856627153315983, 0.0951212405381588, 0.0482516061458583,
                0.137318972929847, 0.761241990602591, 0.101439036467562,
                0.11189821299995, 0.0767994186031903, 0.811302368396859);
            var outsetInv = new D33(
                0.899796955911611, 0.0871996192028351, 0.013003424885555,
                0.11142098895748, 0.875575586156966, 0.0130034248855548,
                0.11142098895748, 0.0871996192028349, 0.801379391839686);

            var rec709ToXyz = RgbToXyz(0.64, 0.33, 0.30, 0.60, 0.15, 0.06);
            var rec2020ToXyz = RgbToXyz(0.708, 0.292, 0.170, 0.797, 0.131, 0.046);
            var rec709To2020 = rec2020ToXyz.Inverse() * rec709ToXyz;

            InputMatrix = (inset * rec709To2020).ToMatrix();
            OutsetMatrix = outsetInv.Inverse().ToMatrix();
            OutputMatrix = rec709To2020.Inverse().ToMatrix();
        }

        /// <summary>AgX base contrast curve (polynomial approximation), input and output in {0 to 1} encoded space.</summary>
        public static float Sigmoid(float x)
        {
            float x2 = x * x, x4 = x2 * x2;
            return 15.5f * x4 * x2 - 40.14f * x4 * x + 31.96f * x4 - 6.868f * x2 * x + 0.4298f * x2 + 0.1191f * x - 0.00232f;
        }

        /// <summary>Scene-linear Rec.709 -> display-linear Rec.709 {0 to 1}. Mirrors the shader.</summary>
        public static Vector3 Map(Vector3 color, in AgxCdl look)
        {
            color = Vector3.Max(color, Vector3.zero);
            color = InputMatrix.MultiplyVector(color);
            color = new Vector3(Sigmoid(Encode(color.x)), Sigmoid(Encode(color.y)), Sigmoid(Encode(color.z)));

            if (!look.IsIdentity)
            {
                color = Vector3.Scale(color, look.slope) + look.offset;
                color = new Vector3(Pow(color.x, look.power.x), Pow(color.y, look.power.y), Pow(color.z, look.power.z));
                float luma = Vector3.Dot(color, k_LookLuma);
                color = Vector3.one * luma + look.saturation * (color - Vector3.one * luma);
            }

            color = OutsetMatrix.MultiplyVector(color);
            color = new Vector3(Pow(color.x, 2.2f), Pow(color.y, 2.2f), Pow(color.z, 2.2f));
            color = OutputMatrix.MultiplyVector(color);
            return new Vector3(Mathf.Clamp01(color.x), Mathf.Clamp01(color.y), Mathf.Clamp01(color.z));
        }

        /// <summary>Log2 encoding into {0 to 1} over [MinEv, MaxEv].</summary>
        public static float Encode(float v) => Mathf.Clamp01((Mathf.Log(Mathf.Max(v, 1e-10f), 2f) - MinEv) / (MaxEv - MinEv));

        static float Pow(float x, float p) => Mathf.Pow(Mathf.Max(x, 0f), p);

        // ---------------------------------------------------------------------------------------------------------------
        // Small double-precision 3x3 helpers (row-major).

        readonly struct D33
        {
            readonly double m00, m01, m02, m10, m11, m12, m20, m21, m22;

            public D33(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22)
            {
                this.m00 = m00; this.m01 = m01; this.m02 = m02;
                this.m10 = m10; this.m11 = m11; this.m12 = m12;
                this.m20 = m20; this.m21 = m21; this.m22 = m22;
            }

            public static D33 operator *(D33 a, D33 b) => new D33(
                a.m00 * b.m00 + a.m01 * b.m10 + a.m02 * b.m20, a.m00 * b.m01 + a.m01 * b.m11 + a.m02 * b.m21, a.m00 * b.m02 + a.m01 * b.m12 + a.m02 * b.m22,
                a.m10 * b.m00 + a.m11 * b.m10 + a.m12 * b.m20, a.m10 * b.m01 + a.m11 * b.m11 + a.m12 * b.m21, a.m10 * b.m02 + a.m11 * b.m12 + a.m12 * b.m22,
                a.m20 * b.m00 + a.m21 * b.m10 + a.m22 * b.m20, a.m20 * b.m01 + a.m21 * b.m11 + a.m22 * b.m21, a.m20 * b.m02 + a.m21 * b.m12 + a.m22 * b.m22);

            public D33 Inverse()
            {
                double c00 = m11 * m22 - m12 * m21, c01 = m12 * m20 - m10 * m22, c02 = m10 * m21 - m11 * m20;
                double inv = 1.0 / (m00 * c00 + m01 * c01 + m02 * c02);
                return new D33(
                    c00 * inv, (m02 * m21 - m01 * m22) * inv, (m01 * m12 - m02 * m11) * inv,
                    c01 * inv, (m00 * m22 - m02 * m20) * inv, (m02 * m10 - m00 * m12) * inv,
                    c02 * inv, (m01 * m20 - m00 * m21) * inv, (m00 * m11 - m01 * m10) * inv);
            }

            public static D33 Diagonal(double x, double y, double z) => new D33(x, 0, 0, 0, y, 0, 0, 0, z);

            public (double, double, double) Multiply(double x, double y, double z) =>
                (m00 * x + m01 * y + m02 * z, m10 * x + m11 * y + m12 * z, m20 * x + m21 * y + m22 * z);

            public Matrix4x4 ToMatrix()
            {
                var m = Matrix4x4.identity;
                m.m00 = (float)m00; m.m01 = (float)m01; m.m02 = (float)m02;
                m.m10 = (float)m10; m.m11 = (float)m11; m.m12 = (float)m12;
                m.m20 = (float)m20; m.m21 = (float)m21; m.m22 = (float)m22;
                return m;
            }
        }

        /// <summary>RGB -> XYZ matrix from xy chromaticities of the primaries, D65 white.</summary>
        static D33 RgbToXyz(double rx, double ry, double gx, double gy, double bx, double by)
        {
            const double wx = 0.3127, wy = 0.3290;
            var primaries = new D33(
                rx / ry, gx / gy, bx / by,
                1.0, 1.0, 1.0,
                (1 - rx - ry) / ry, (1 - gx - gy) / gy, (1 - bx - by) / by);
            var (sr, sg, sb) = primaries.Inverse().Multiply(wx / wy, 1.0, (1 - wx - wy) / wy);
            return primaries * D33.Diagonal(sr, sg, sb);
        }
    }
}
