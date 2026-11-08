using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace FidelityFX.Lpm
{
    public enum LpmTonemappingMode
    {
        None = 0,
        [InspectorName("AMD FidelityFX LPM")] FidelityFXLpm = 1,
    }

    [Serializable]
    public sealed class LpmTonemappingModeParameter : VolumeParameter<LpmTonemappingMode>
    {
        public LpmTonemappingModeParameter(LpmTonemappingMode value, bool overrideState = false) : base(value, overrideState) { }
    }

    /// <summary>
    /// AMD FidelityFX LPM (Luma Preserving Mapper) as an HDRP custom post process.
    /// Runs at "After Post Process" (after HDRP's Uber pass, before FXAA and the final pass), so HDRP's own
    /// Tonemapping override should be set to None. Color grading (Color Adjustments, White Balance, etc.) still
    /// applies before LPM, through HDRP's grading LUT.
    /// </summary>
    [Serializable, VolumeComponentMenu("Post-processing/Custom/FidelityFX LPM Tonemapping")]
    [SupportedOnRenderPipeline(typeof(HDRenderPipelineAsset))]
    public sealed class LpmTonemapping : CustomPostProcessVolumeComponent, IPostProcessComponent
    {
        public const string ShaderName = "Hidden/FidelityFX/LpmTonemapping";

        /// <summary>
        /// Default crosstalk {1, 1, 1/32}: the first step of AMD's tuning guide (red -> yellow -> white, blues keep their hue).
        /// AMD's setup example uses {1, 1/2, 1/32}, which tints over-exposed blues towards purple (a bright sky turns lavender).
        /// </summary>
        public static readonly Vector3 DefaultCrosstalk = new Vector3(1f, 1f, 1f / 32f);

        /// <summary>Linear value at which HDRP's grading LUT (LogC) saturates. HDRP clamps the input of this effect here.</summary>
        public const float HdrpLutMaxInput = 58.85f;

        [Tooltip("Selects the tonemapper. None disables this effect.")]
        public LpmTonemappingModeParameter mode = new LpmTonemappingModeParameter(LpmTonemappingMode.None);

        [Tooltip("Exposure applied to the HDR input right before mapping, in EV. LPM keeps mid-gray at 0.18 while ACES darkens mid-tones, so -0.5 to -0.75 gets close to ACES brightness. Note HDRP has already clamped the input at ~59 (grading LUT range), so to recover highlights lower the exposure earlier (Exposure / Color Adjustments > Post Exposure).")]
        public ClampedFloatParameter exposure = new ClampedFloatParameter(0f, -8f, 8f);

        // Defaults: HDR Max 64 / Curve Exposure 6 fits HDRP's ~59 input clamp (the whole curve gets used) and keeps
        // 0.18 -> 0.18; Contrast 0.3 is the default of AMD's LPM sample. With Contrast 0 the curve is flatter than linear at mid-gray.
        [Tooltip("Maximum linear input value (LPM 'hdrMax'). It maps to output white; anything above is clamped. HDRP clamps this effect's input at ~59, so values much higher than 64 leave the top of the curve unused.")]
        public ClampedFloatParameter hdrMax = new ClampedFloatParameter(64f, 1f, 4096f);

        [Tooltip("Number of stops between HDR Maximum and the 18% mid-gray on input (LPM 'exposure'). With the defaults (64, 6 stops) an input of 0.18 maps to 0.18; use 'Anchor 0.18' after changing HDR Maximum.")]
        public ClampedFloatParameter curveExposure = new ClampedFloatParameter(6f, 1f, 16f);

        [Tooltip("Extra contrast: 0 = none, 1 = maximum. Mid-tone contrast is roughly 0.82 x (1 + Contrast), so 0.25-0.3 is about neutral (AMD's sample uses 0.3). In LPM this also raises saturation; compensate with negative Saturation if needed.")]
        public ClampedFloatParameter contrast = new ClampedFloatParameter(0.3f, 0f, 1f);

        [Tooltip("Shoulder shaping. 1 = no change (fast path). Higher values roll the highlights off more abruptly.")]
        public ClampedFloatParameter shoulderContrast = new ClampedFloatParameter(1f, 0.5f, 2f);

        [Tooltip("Per-channel saturation: < 0 decreases, 0 = no change, > 0 increases. Luma preserving.")]
        public Vector3Parameter saturation = new Vector3Parameter(Vector3.zero);

        [Tooltip("Shapes how color over-exposes. One channel should be 1, the others <= 1 (never 0). Lower blue keeps more saturation in bright areas (tints towards yellow); then lower red to tint blues towards cyan, or green to tint them towards purple. The default {1, 1, 1/32} keeps blues blue; AMD's example {1, 1/2, 1/32} turns bright skies lavender.")]
        public Vector3Parameter crosstalk = new Vector3Parameter(DefaultCrosstalk);

        [Tooltip("Not part of AMD's LPM (0 = original LPM). LPM keeps the hue and saturation of highlights, so warm sunlight on a warm surface stays orange instead of going towards white like with ACES. This pulls bright colors towards white as they approach the top of the curve, keeping their luminance. 1 gets close to ACES.")]
        public ClampedFloatParameter highlightDesaturation = new ClampedFloatParameter(0f, 0f, 1f);

        [Tooltip("Output brightness (linear, 0.18 = mid-gray) where Highlight Desaturation starts. It reaches full strength at white.")]
        public ClampedFloatParameter highlightDesaturationStart = new ClampedFloatParameter(0.18f, 0f, 0.9f);

        static class ShaderIDs
        {
            public static readonly int MainTex = Shader.PropertyToID("_MainTex");
            public static readonly int SaturationContrast = Shader.PropertyToID("_LpmSaturationContrast");
            public static readonly int ToneScaleBias = Shader.PropertyToID("_LpmToneScaleBias");
            public static readonly int LumaT = Shader.PropertyToID("_LpmLumaT");
            public static readonly int RcpLumaT = Shader.PropertyToID("_LpmRcpLumaT");
            public static readonly int Crosstalk = Shader.PropertyToID("_LpmCrosstalk");
            public static readonly int Input = Shader.PropertyToID("_LpmInput");
            public static readonly int HighlightDesat = Shader.PropertyToID("_LpmHighlightDesat");
        }

        const int k_PassLpm = 0;
        const int k_PassCopy = 1;

        Material m_Material;
        MaterialPropertyBlock m_Properties;

        public bool IsActive() => m_Material != null && mode.value == LpmTonemappingMode.FidelityFXLpm;

        // Do not forget to add this type to the "After Post Process" list in
        // Project Settings > Graphics > HDRP Global Settings > Custom Post Process Orders (the inspector has a button for it).
        public override CustomPostProcessInjectionPoint injectionPoint => CustomPostProcessInjectionPoint.AfterPostProcess;

        /// <summary>Builds the LPM control block from the current parameter values.</summary>
        public LpmConstants ComputeConstants()
        {
            var k = LpmCore.Setup709(hdrMax.value, curveExposure.value, contrast.value, shoulderContrast.value, saturation.value, crosstalk.value);
            k.highlightDesaturation = new Vector2(highlightDesaturation.value, highlightDesaturationStart.value);
            return k;
        }

        public override void Setup()
        {
            var shader = Shader.Find(ShaderName);
            if (shader != null)
                m_Material = CoreUtils.CreateEngineMaterial(shader);
            else
                Debug.LogError($"Unable to find shader '{ShaderName}'. The FidelityFX LPM Tonemapping post process can't render.");

            m_Properties = new MaterialPropertyBlock();
        }

        public override void Render(CommandBuffer cmd, HDCamera camera, RTHandle source, RTHandle destination)
        {
            if (m_Material == null)
                return;

            m_Properties.Clear();
            m_Properties.SetTexture(ShaderIDs.MainTex, source);

            // This setup targets SDR output (LPM_CONFIG_709_709). When HDRP drives an HDR display its own HDR
            // tonemapping/encoding is in charge, so just pass the image through instead of breaking it.
            bool hdrOutput = camera.camera.cameraType == CameraType.Game
                && SystemInfo.hdrDisplaySupportFlags.HasFlag(HDRDisplaySupportFlags.Supported)
                && HDROutputSettings.main.active;

            if (!hdrOutput)
            {
                var k = ComputeConstants();
                m_Properties.SetVector(ShaderIDs.SaturationContrast, new Vector4(k.saturation.x, k.saturation.y, k.saturation.z, k.contrast));
                m_Properties.SetVector(ShaderIDs.ToneScaleBias, new Vector4(k.toneScaleBias.x, k.toneScaleBias.y, k.shoulderContrast, k.shoulder ? 1f : 0f));
                m_Properties.SetVector(ShaderIDs.LumaT, k.lumaT);
                m_Properties.SetVector(ShaderIDs.RcpLumaT, k.rcpLumaT);
                m_Properties.SetVector(ShaderIDs.Crosstalk, k.crosstalk);
                m_Properties.SetVector(ShaderIDs.Input, new Vector4(Mathf.Pow(2f, exposure.value), hdrMax.value, 0f, 0f));
                m_Properties.SetVector(ShaderIDs.HighlightDesat, new Vector4(k.highlightDesaturation.x, k.highlightDesaturation.y, 0f, 0f));
            }

            HDUtils.DrawFullScreen(cmd, m_Material, destination, m_Properties, hdrOutput ? k_PassCopy : k_PassLpm);
        }

        public override void Cleanup()
        {
            CoreUtils.Destroy(m_Material);
            m_Material = null;
        }
    }
}
