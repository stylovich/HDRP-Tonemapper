using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace AgX
{
    public enum AgxTonemappingMode
    {
        None = 0,
        [InspectorName("AgX")] AgX = 1,
    }

    [Serializable]
    public sealed class AgxTonemappingModeParameter : VolumeParameter<AgxTonemappingMode>
    {
        public AgxTonemappingModeParameter(AgxTonemappingMode value, bool overrideState = false) : base(value, overrideState) { }
    }

    [Serializable]
    public sealed class AgxLookParameter : VolumeParameter<AgxLook>
    {
        public AgxLookParameter(AgxLook value, bool overrideState = false) : base(value, overrideState) { }
    }

    /// <summary>
    /// AgX tonemapping (Blender 4.x style, Rec.2020 inset/outset) as an HDRP custom post process.
    /// Runs at "After Post Process", so HDRP's own Tonemapping override should be set to None, and no other
    /// tonemapping post process (e.g. FidelityFX LPM) should be active at the same time.
    /// </summary>
    [Serializable, VolumeComponentMenu("Post-processing/Custom/AgX Tonemapping")]
    [SupportedOnRenderPipeline(typeof(HDRenderPipelineAsset))]
    public sealed class AgxTonemapping : CustomPostProcessVolumeComponent, IPostProcessComponent
    {
        public const string ShaderName = "Hidden/AgX/Tonemapping";

        [Tooltip("Selects the tonemapper. None disables this effect.")]
        public AgxTonemappingModeParameter mode = new AgxTonemappingModeParameter(AgxTonemappingMode.None);

        [Tooltip("Exposure applied to the HDR input before mapping, in EV. AgX maps 0.18 to about 0.5 on screen (sRGB), brighter than ACES; about -1 EV gets close to ACES brightness.")]
        public ClampedFloatParameter exposure = new ClampedFloatParameter(0f, -8f, 8f);

        [Tooltip("AgX look (ASC CDL in AgX's encoded space). None = AgX Base. Punchy: more contrast and saturation. Golden: warm, faded. Custom: use the values below.")]
        public AgxLookParameter look = new AgxLookParameter(AgxLook.None);

        [Tooltip("Custom look: per-channel slope (multiplier).")]
        public Vector3Parameter slope = new Vector3Parameter(Vector3.one);

        [Tooltip("Custom look: per-channel offset (added after the slope).")]
        public Vector3Parameter offset = new Vector3Parameter(Vector3.zero);

        [Tooltip("Custom look: per-channel power (> 1 adds contrast in the encoded space).")]
        public Vector3Parameter power = new Vector3Parameter(Vector3.one);

        [Tooltip("Custom look: saturation (1 = unchanged).")]
        public ClampedFloatParameter saturation = new ClampedFloatParameter(1f, 0f, 2f);

        static class ShaderIDs
        {
            public static readonly int MainTex = Shader.PropertyToID("_MainTex");
            public static readonly int InputMatrix = Shader.PropertyToID("_AgxInputMatrix");
            public static readonly int OutsetMatrix = Shader.PropertyToID("_AgxOutsetMatrix");
            public static readonly int OutputMatrix = Shader.PropertyToID("_AgxOutputMatrix");
            public static readonly int Params = Shader.PropertyToID("_AgxParams");
            public static readonly int Slope = Shader.PropertyToID("_AgxSlope");
            public static readonly int Offset = Shader.PropertyToID("_AgxOffset");
            public static readonly int Power = Shader.PropertyToID("_AgxPower");
        }

        const int k_PassAgx = 0;
        const int k_PassCopy = 1;

        Material m_Material;
        MaterialPropertyBlock m_Properties;

        public bool IsActive() => m_Material != null && mode.value == AgxTonemappingMode.AgX;

        // Do not forget to add this type to the "After Post Process" list in
        // Project Settings > Graphics > HDRP Global Settings > Custom Post Process Orders (the inspector has a button for it).
        public override CustomPostProcessInjectionPoint injectionPoint => CustomPostProcessInjectionPoint.AfterPostProcess;

        /// <summary>The ASC CDL for the selected look.</summary>
        public AgxCdl GetLook() => GetLook(look.value, slope.value, offset.value, power.value, saturation.value);

        public static AgxCdl GetLook(AgxLook look, Vector3 slope, Vector3 offset, Vector3 power, float saturation)
        {
            switch (look)
            {
                case AgxLook.Punchy: return AgxCdl.Punchy;
                case AgxLook.Golden: return AgxCdl.Golden;
                case AgxLook.Custom: return new AgxCdl(slope, offset, power, saturation);
                default: return AgxCdl.Identity;
            }
        }

        public override void Setup()
        {
            var shader = Shader.Find(ShaderName);
            if (shader != null)
                m_Material = CoreUtils.CreateEngineMaterial(shader);
            else
                Debug.LogError($"Unable to find shader '{ShaderName}'. The AgX Tonemapping post process can't render.");

            m_Properties = new MaterialPropertyBlock();
        }

        public override void Render(CommandBuffer cmd, HDCamera camera, RTHandle source, RTHandle destination)
        {
            if (m_Material == null)
                return;

            m_Properties.Clear();
            m_Properties.SetTexture(ShaderIDs.MainTex, source);

            // SDR only: when HDRP drives an HDR display its own HDR tonemapping/encoding is in charge, so pass through.
            bool hdrOutput = camera.camera.cameraType == CameraType.Game
                && SystemInfo.hdrDisplaySupportFlags.HasFlag(HDRDisplaySupportFlags.Supported)
                && HDROutputSettings.main.active;

            if (!hdrOutput)
            {
                var cdl = GetLook();
                m_Properties.SetMatrix(ShaderIDs.InputMatrix, AgxCore.InputMatrix);
                m_Properties.SetMatrix(ShaderIDs.OutsetMatrix, AgxCore.OutsetMatrix);
                m_Properties.SetMatrix(ShaderIDs.OutputMatrix, AgxCore.OutputMatrix);
                m_Properties.SetVector(ShaderIDs.Params, new Vector4(Mathf.Pow(2f, exposure.value), cdl.IsIdentity ? 0f : 1f, cdl.saturation, 0f));
                m_Properties.SetVector(ShaderIDs.Slope, cdl.slope);
                m_Properties.SetVector(ShaderIDs.Offset, cdl.offset);
                m_Properties.SetVector(ShaderIDs.Power, cdl.power);
            }

            HDUtils.DrawFullScreen(cmd, m_Material, destination, m_Properties, hdrOutput ? k_PassCopy : k_PassAgx);
        }

        public override void Cleanup()
        {
            CoreUtils.Destroy(m_Material);
            m_Material = null;
        }
    }
}
