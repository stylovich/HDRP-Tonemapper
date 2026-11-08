using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.Rendering.HighDefinition;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace FidelityFX.Lpm.Editors
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(LpmTonemapping))]
    sealed class LpmTonemappingEditor : CustomPostProcessVolumeComponentEditor
    {
        // Ramp range: 2^-8 (0.004) to 65504 (FP16 max), log2 scale.
        const float k_RampMinLog2 = -8f;
        const float k_RampMaxLog2 = 15.99934f;
        const int k_RampWidth = 512;
        const int k_StripWidth = 256;
        const float k_StripStops = 4f;

        const string k_TestColorKey = "FidelityFX.Lpm.TestColor";
        const string k_SimulateClampKey = "FidelityFX.Lpm.SimulateHdrpClamp";
        static readonly Vector3 k_DefaultTestColor = new Vector3(4f, 0.4f, 0.04f);

        static readonly float[] k_BlueWeights = { 1f / 8f, 1f / 16f, 1f / 32f, 1f / 64f, 1f / 128f };
        static readonly string[] k_BlueWeightLabels = { "1/8", "1/16", "1/32", "1/64", "1/128" };

        SerializedDataParameter m_Mode, m_Exposure, m_HdrMax, m_CurveExposure, m_Contrast, m_ShoulderContrast, m_Saturation, m_Crosstalk;
        SerializedDataParameter m_HighlightDesaturation, m_HighlightDesaturationStart;

        Texture2D m_RampR, m_RampG, m_RampB, m_Strip;
        int m_PreviewHash;
        readonly Vector3[] m_CurvePoints = new Vector3[160];

        public override void OnEnable()
        {
            base.OnEnable();
            var o = new PropertyFetcher<LpmTonemapping>(serializedObject);
            m_Mode = Unpack(o.Find(x => x.mode));
            m_Exposure = Unpack(o.Find(x => x.exposure));
            m_HdrMax = Unpack(o.Find(x => x.hdrMax));
            m_CurveExposure = Unpack(o.Find(x => x.curveExposure));
            m_Contrast = Unpack(o.Find(x => x.contrast));
            m_ShoulderContrast = Unpack(o.Find(x => x.shoulderContrast));
            m_Saturation = Unpack(o.Find(x => x.saturation));
            m_Crosstalk = Unpack(o.Find(x => x.crosstalk));
            m_HighlightDesaturation = Unpack(o.Find(x => x.highlightDesaturation));
            m_HighlightDesaturationStart = Unpack(o.Find(x => x.highlightDesaturationStart));
        }

        public override void OnDisable()
        {
            base.OnDisable();
            CoreUtils.Destroy(m_RampR);
            CoreUtils.Destroy(m_RampG);
            CoreUtils.Destroy(m_RampB);
            CoreUtils.Destroy(m_Strip);
            m_RampR = m_RampG = m_RampB = m_Strip = null;
        }

        public override void OnInspectorGUI()
        {
            DrawRegistrationBox();
            DrawHdrpTonemappingBox();

            PropertyField(m_Mode, Label(m_Mode, "Mode"));
            PropertyField(m_Exposure, Label(m_Exposure, "Exposure"));
            PropertyField(m_HdrMax, Label(m_HdrMax, "HDR Maximum"));
            PropertyField(m_CurveExposure, Label(m_CurveExposure, "Curve Exposure (Stops)"));
            PropertyField(m_Contrast, Label(m_Contrast, "Contrast"));
            PropertyField(m_ShoulderContrast, Label(m_ShoulderContrast, "Shoulder Contrast"));
            PropertyField(m_Saturation, Label(m_Saturation, "Saturation (RGB)"));
            PropertyField(m_Crosstalk, Label(m_Crosstalk, "Crosstalk (RGB)"));

            var settings = ReadSettings();

            DrawMidGrayInfo(settings);
            DrawCrosstalkPresets();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(new GUIContent("Extension","Optional controls added on top of the original algorithm. At 0 the mapping is AMD's LPM."), EditorStyles.boldLabel);
            PropertyField(m_HighlightDesaturation, Label(m_HighlightDesaturation, "Highlight Desaturation"));
            using (new EditorGUI.DisabledScope(m_HighlightDesaturation.value.floatValue <= 0f))
            using (new EditorGUI.IndentLevelScope())
                PropertyField(m_HighlightDesaturationStart, Label(m_HighlightDesaturationStart, "Start"));

            DrawToneCurve(settings);
            DrawHdrRamps(settings);
            DrawTestColor(settings);
        }

        static GUIContent Label(SerializedDataParameter p, string text)
            => new GUIContent(text, p.GetAttribute<TooltipAttribute>()?.tooltip);

        // ---------------------------------------------------------------------------------------------------------------
        // Settings snapshot (taken from the serialized values so previews update while dragging).

        struct Settings
        {
            public LpmConstants constants;
            public float preExposure, hdrMax, curveExposure;
            public bool simulateClamp;
        }

        Settings ReadSettings()
        {
            float hdrMax = m_HdrMax.value.floatValue;
            float curveExposure = m_CurveExposure.value.floatValue;
            var constants = LpmCore.Setup709(hdrMax, curveExposure, m_Contrast.value.floatValue, m_ShoulderContrast.value.floatValue,
                m_Saturation.value.vector3Value, m_Crosstalk.value.vector3Value);
            constants.highlightDesaturation = new Vector2(m_HighlightDesaturation.value.floatValue, m_HighlightDesaturationStart.value.floatValue);
            return new Settings
            {
                constants = constants,
                preExposure = Mathf.Pow(2f, m_Exposure.value.floatValue),
                hdrMax = hdrMax,
                curveExposure = curveExposure,
                simulateClamp = EditorPrefs.GetBool(k_SimulateClampKey, true),
            };
        }

        /// <summary>Scene-linear color -> what this effect outputs (same steps as the shader).</summary>
        static Vector3 Evaluate(Vector3 sceneLinear, in Settings s)
        {
            if (s.simulateClamp) // HDRP's grading LUT saturates in LogC before this effect runs.
                sceneLinear = Vector3.Min(sceneLinear, Vector3.one * LpmTonemapping.HdrpLutMaxInput);
            Vector3 c = Vector3.Min(sceneLinear * s.preExposure, Vector3.one * s.hdrMax);
            return LpmCore.Map(c, s.constants);
        }

        // ---------------------------------------------------------------------------------------------------------------
        // Setup checks

        void DrawRegistrationBox()
        {
            bool? registered = CustomPostProcessRegistry.IsRegistered(typeof(LpmTonemapping));
            if (registered != false)
                return;

            EditorGUILayout.HelpBox("This custom post process is not registered in Project Settings > Graphics > HDRP Global Settings > Custom Post Process Orders, so HDRP will not render it.", MessageType.Error);
            if (GUILayout.Button("Register in \"After Post Process\""))
            {
                if (!CustomPostProcessRegistry.RegisterAfterPostProcess(typeof(LpmTonemapping)))
                    Debug.LogError("FidelityFX LPM: could not register the custom post process automatically. Add it manually in Project Settings > Graphics > HDRP Global Settings > Custom Post Process Orders > After Post Process.");
            }
            EditorGUILayout.Space();
        }

        void DrawHdrpTonemappingBox()
        {
            if (m_Mode.value.intValue != (int)LpmTonemappingMode.FidelityFXLpm)
                return;

            var stack = VolumeManager.instance.isInitialized ? VolumeManager.instance.stack : null;
            var hdrpTonemapping = stack?.GetComponent<Tonemapping>();
            if (hdrpTonemapping == null || !hdrpTonemapping.IsActive())
                return;

            EditorGUILayout.HelpBox($"HDRP Tonemapping is also active ({hdrpTonemapping.mode.value}), so the image gets tonemapped twice. Set its Mode to None; LPM replaces it.", MessageType.Warning);

            var profile = GetOwningProfile();
            if (profile != null && GUILayout.Button("Set HDRP Tonemapping to None in this profile"))
                OverrideHdrpTonemappingToNone(profile);
            EditorGUILayout.Space();
        }

        VolumeProfile GetOwningProfile()
        {
            string path = AssetDatabase.GetAssetPath(target);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadMainAssetAtPath(path) as VolumeProfile;
        }

        static void OverrideHdrpTonemappingToNone(VolumeProfile profile)
        {
            Undo.RecordObject(profile, "Set HDRP Tonemapping to None");
            if (!profile.TryGet<Tonemapping>(out var tonemapping))
            {
                tonemapping = profile.Add<Tonemapping>();
                tonemapping.name = nameof(Tonemapping);
                tonemapping.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
                if (EditorUtility.IsPersistent(profile))
                    AssetDatabase.AddObjectToAsset(tonemapping, profile);
                Undo.RegisterCreatedObjectUndo(tonemapping, "Set HDRP Tonemapping to None");
            }

            Undo.RecordObject(tonemapping, "Set HDRP Tonemapping to None");
            tonemapping.active = true;
            tonemapping.mode.overrideState = true;
            tonemapping.mode.value = TonemappingMode.None;
            EditorUtility.SetDirty(tonemapping);
            EditorUtility.SetDirty(profile);
        }

        // ---------------------------------------------------------------------------------------------------------------
        // Mid-gray info

        void DrawMidGrayInfo(in Settings s)
        {
            float midIn = LpmCore.MidGrayInput(s.hdrMax, s.curveExposure);
            float sceneMid = midIn / s.preExposure;
            float whiteAtLimit = Evaluate(Vector3.one * 1e4f, s).y;

            EditorGUILayout.Space(2);
            using (new EditorGUILayout.HorizontalScope())
            {
                string limit = s.simulateClamp ? $"brightest output {whiteAtLimit:0.000}" : $"white at {s.hdrMax / s.preExposure:0.#}";
                EditorGUILayout.LabelField(
                    new GUIContent($"Scene mid-gray {sceneMid:0.###} → 0.18 · {limit}",
                        "Scene-linear value that LPM maps to 18% gray, and the brightest gray this effect can output (HDRP clamps its input at ~59 with Tonemapping None)."),
                    EditorStyles.miniLabel);

                if (GUILayout.Button(new GUIContent("Anchor 0.18", "Sets Curve Exposure to log2(HDR Maximum) so that an input of 0.18 maps to 0.18 (ignoring Exposure)."), EditorStyles.miniButton, GUILayout.Width(80)))
                {
                    m_CurveExposure.overrideState.boolValue = true;
                    m_CurveExposure.value.floatValue = Mathf.Clamp(Mathf.Log(s.hdrMax, 2f), 1f, 16f);
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------------
        // Crosstalk presets (tuning guide from ffx_lpm.h)

        void DrawCrosstalkPresets()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(new GUIContent("Crosstalk presets (AMD tuning guide)",
                "Crosstalk shapes how colors over-expose. {1,1,1} fully desaturates; lowering blue keeps saturation (tint to yellow); then lowering red tints blues to cyan, lowering green tints them to purple."), EditorStyles.boldLabel);

            Vector3 ct = m_Crosstalk.value.vector3Value;
            float blue = ct.z;

            using (new EditorGUILayout.HorizontalScope())
            {
                PresetButton("Neutral", "Red → yellow → white, no blue tint {1, 1, b}. Default (with b = 1/32).", new Vector3(1f, 1f, blue), ct);
                PresetButton("Blue → purple", "{1, 1/2, b}: AMD's setup example (with b = 1/32). Bright skies turn lavender.", new Vector3(1f, 0.5f, blue), ct);
                PresetButton("Blue → cyan", "{1/2, 1, b}", new Vector3(0.5f, 1f, blue), ct);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                PresetButton("Strong purple", "{1, 1/4, 1/128}", new Vector3(1f, 0.25f, 1f / 128f), ct);
                PresetButton("Desaturate", "{1, 1, 1}: fully desaturates on over-exposure (not recommended)", Vector3.one, ct);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("Blue weight", "Lower blue = more saturation kept on over-exposure."));
                for (int i = 0; i < k_BlueWeights.Length; i++)
                {
                    bool on = Mathf.Abs(blue - k_BlueWeights[i]) < 1e-5f;
                    if (GUILayout.Toggle(on, k_BlueWeightLabels[i], EditorStyles.miniButton) && !on)
                        SetCrosstalk(new Vector3(ct.x, ct.y, k_BlueWeights[i]));
                }
            }
        }

        void PresetButton(string label, string tooltip, Vector3 value, Vector3 current)
        {
            bool on = (value - current).sqrMagnitude < 1e-10f;
            if (GUILayout.Toggle(on, new GUIContent(label, tooltip), EditorStyles.miniButton) && !on)
                SetCrosstalk(value);
        }

        void SetCrosstalk(Vector3 value)
        {
            m_Crosstalk.overrideState.boolValue = true;
            m_Crosstalk.value.vector3Value = value;
        }

        // ---------------------------------------------------------------------------------------------------------------
        // Tone curve (neutral gray, display-encoded output vs. stops around scene mid-gray)

        void DrawToneCurve(in Settings s)
        {
            const float minEv = -8f, maxEv = 10f;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Tone curve (neutral gray)", EditorStyles.boldLabel);
            Rect rect = EditorGUI.IndentedRect(GUILayoutUtility.GetRect(10f, 120f, GUILayout.ExpandWidth(true)));
            Rect labels = EditorGUI.IndentedRect(GUILayoutUtility.GetRect(10f, 14f, GUILayout.ExpandWidth(true)));
            if (Event.current.type != EventType.Repaint)
                return;

            bool pro = EditorGUIUtility.isProSkin;
            EditorGUI.DrawRect(rect, pro ? new Color(0.1f, 0.1f, 0.1f) : new Color(0.75f, 0.75f, 0.75f));
            var grid = pro ? new Color(1f, 1f, 1f, 0.08f) : new Color(0f, 0f, 0f, 0.12f);

            float X(float ev) => Mathf.Lerp(rect.xMin, rect.xMax, Mathf.InverseLerp(minEv, maxEv, ev));
            float Y(float v) => Mathf.Lerp(rect.yMax, rect.yMin, v);

            for (float ev = minEv; ev <= maxEv; ev += 2f)
                EditorGUI.DrawRect(new Rect(X(ev), rect.yMin, 1f, rect.height), grid);
            for (float v = 0.25f; v < 1f; v += 0.25f)
                EditorGUI.DrawRect(new Rect(rect.xMin, Y(v), rect.width, 1f), grid);

            // Mid-gray (0 EV) and the HDRP input limit.
            EditorGUI.DrawRect(new Rect(X(0f), rect.yMin, 1f, rect.height), pro ? new Color(1f, 1f, 1f, 0.25f) : new Color(0f, 0f, 0f, 0.3f));
            float limitEv = Mathf.Log(LpmTonemapping.HdrpLutMaxInput / 0.18f, 2f);
            if (s.simulateClamp && limitEv < maxEv)
                EditorGUI.DrawRect(new Rect(X(limitEv), rect.yMin, 1f, rect.height), new Color(1f, 0.45f, 0.2f, 0.6f));

            for (int i = 0; i < m_CurvePoints.Length; i++)
            {
                float ev = Mathf.Lerp(minEv, maxEv, i / (m_CurvePoints.Length - 1f));
                float v = 0.18f * Mathf.Pow(2f, ev);
                float y = Mathf.LinearToGammaSpace(Evaluate(new Vector3(v, v, v), s).y);
                m_CurvePoints[i] = new Vector3(X(ev), Y(Mathf.Clamp01(y)), 0f);
            }

            Handles.color = pro ? new Color(0.95f, 0.95f, 0.95f) : new Color(0.1f, 0.1f, 0.1f);
            Handles.DrawAAPolyLine(2f, m_CurvePoints);
            Handles.color = Color.white;

            var mini = EditorStyles.centeredGreyMiniLabel;
            GUI.Label(new Rect(labels.xMin, labels.y, 60f, labels.height), $"{minEv:+0;-0} EV", LeftMini());
            GUI.Label(new Rect(X(0f) - 40f, labels.y, 80f, labels.height), "mid-gray", mini);
            if (s.simulateClamp && limitEv < maxEv)
                GUI.Label(new Rect(X(limitEv) - 40f, labels.y, 80f, labels.height), "HDRP limit", mini);
            GUI.Label(new Rect(labels.xMax - 60f, labels.y, 60f, labels.height), $"{maxEv:+0;-0} EV", RightMini());
        }

        // ---------------------------------------------------------------------------------------------------------------
        // HDR ramps of pure primaries

        void DrawHdrRamps(in Settings s)
        {
            UpdatePreviewTextures(s);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("HDR color preview (pure RGB)", EditorStyles.boldLabel);
                bool clamp = GUILayout.Toggle(s.simulateClamp, new GUIContent("HDRP clamp", "Simulate the ~59 input clamp HDRP's grading LUT applies before this effect runs."), EditorStyles.miniButton, GUILayout.Width(80));
                if (clamp != s.simulateClamp)
                    EditorPrefs.SetBool(k_SimulateClampKey, clamp);
            }

            Rect first = default, last = default;
            DrawRampRow("R", m_RampR, ref first, ref last);
            DrawRampRow("G", m_RampG, ref first, ref last);
            DrawRampRow("B", m_RampB, ref first, ref last);

            if (Event.current.type == EventType.Repaint && s.simulateClamp)
            {
                float f = Mathf.InverseLerp(k_RampMinLog2, k_RampMaxLog2, Mathf.Log(LpmTonemapping.HdrpLutMaxInput, 2f));
                float x = Mathf.Lerp(first.xMin, first.xMax, f);
                EditorGUI.DrawRect(new Rect(x, first.yMin, 1f, last.yMax - first.yMin), new Color(1f, 0.45f, 0.2f, 0.8f));
            }

            // Axis labels at 2^-8, 2^0, 2^8, 65504.
            Rect axis = GUILayoutUtility.GetRect(10f, 14f, GUILayout.ExpandWidth(true));
            axis = EditorGUI.IndentedRect(axis);
            axis.xMin = first.xMin;
            float Ax(float log2) => Mathf.Lerp(axis.xMin, axis.xMax, Mathf.InverseLerp(k_RampMinLog2, k_RampMaxLog2, log2));
            GUI.Label(new Rect(axis.xMin, axis.y, 50f, axis.height), "0.004", LeftMini());
            GUI.Label(new Rect(Ax(0f) - 25f, axis.y, 50f, axis.height), "1", EditorStyles.centeredGreyMiniLabel);
            GUI.Label(new Rect(Ax(8f) - 25f, axis.y, 50f, axis.height), "256", EditorStyles.centeredGreyMiniLabel);
            GUI.Label(new Rect(axis.xMax - 50f, axis.y, 50f, axis.height), "65504", RightMini());

            string caption = s.simulateClamp ? "Linear input intensity, log scale · orange = HDRP input limit (~59)" : "Linear input intensity, logarithmic scale";
            EditorGUILayout.LabelField(caption, EditorStyles.centeredGreyMiniLabel);
        }

        static void DrawRampRow(string label, Texture2D tex, ref Rect first, ref Rect last)
        {
            Rect row = GUILayoutUtility.GetRect(10f, 20f, GUILayout.ExpandWidth(true));
            row = EditorGUI.IndentedRect(row);
            var labelRect = new Rect(row.x, row.y, 16f, row.height);
            var rampRect = new Rect(row.x + 18f, row.y + 1f, row.width - 18f, row.height - 2f);
            GUI.Label(labelRect, label, EditorStyles.miniLabel);
            if (Event.current.type == EventType.Repaint && tex != null)
                GUI.DrawTexture(rampRect, tex, ScaleMode.StretchToFill, false);
            if (first == default) first = rampRect;
            last = rampRect;
        }

        // ---------------------------------------------------------------------------------------------------------------
        // Test color

        void DrawTestColor(in Settings s)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("HDR test color (scene linear RGB)", EditorStyles.boldLabel);

            Vector3 testColor = SessionState.GetVector3(k_TestColorKey, k_DefaultTestColor);
            EditorGUI.BeginChangeCheck();
            testColor = EditorGUILayout.Vector3Field(GUIContent.none, testColor);
            if (EditorGUI.EndChangeCheck())
            {
                testColor = Vector3.Max(testColor, Vector3.zero);
                SessionState.SetVector3(k_TestColorKey, testColor);
                UpdatePreviewTextures(s, force: true);
            }

            Vector3 mapped = Evaluate(testColor, s);
            EditorGUILayout.LabelField($"Mapped result (SDR, linear {mapped.x:0.000}, {mapped.y:0.000}, {mapped.z:0.000})", EditorStyles.miniLabel);
            Rect swatch = EditorGUI.IndentedRect(GUILayoutUtility.GetRect(10f, 28f, GUILayout.ExpandWidth(true)));
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(swatch, new Color(mapped.x, mapped.y, mapped.z, 1f).gamma);

            EditorGUILayout.LabelField($"Same color from 1/{1 << (int)k_StripStops} to {1 << (int)k_StripStops} times its brightness", EditorStyles.miniLabel);
            Rect strip = EditorGUI.IndentedRect(GUILayoutUtility.GetRect(10f, 22f, GUILayout.ExpandWidth(true)));
            if (Event.current.type == EventType.Repaint && m_Strip != null)
            {
                GUI.DrawTexture(strip, m_Strip, ScaleMode.StretchToFill, false);
                EditorGUI.DrawRect(new Rect(strip.center.x, strip.yMin, 1f, strip.height), new Color(0f, 0f, 0f, 0.6f));
            }
            Rect axis = EditorGUI.IndentedRect(GUILayoutUtility.GetRect(10f, 14f, GUILayout.ExpandWidth(true)));
            GUI.Label(new Rect(axis.xMin, axis.y, 90f, axis.height), $"1/{1 << (int)k_StripStops} (-{k_StripStops:0} EV)", LeftMini());
            GUI.Label(new Rect(axis.center.x - 45f, axis.y, 90f, axis.height), "1x (input)", EditorStyles.centeredGreyMiniLabel);
            GUI.Label(new Rect(axis.xMax - 90f, axis.y, 90f, axis.height), $"{1 << (int)k_StripStops}x (+{k_StripStops:0} EV)", RightMini());
        }

        // ---------------------------------------------------------------------------------------------------------------
        // Preview textures

        void UpdatePreviewTextures(in Settings s, bool force = false)
        {
            Vector3 testColor = SessionState.GetVector3(k_TestColorKey, k_DefaultTestColor);
            var k = s.constants;
            var h = new HashCode();
            h.Add(k.saturation); h.Add(k.contrast); h.Add(k.toneScaleBias); h.Add(k.shoulderContrast);
            h.Add(k.crosstalk); h.Add(k.highlightDesaturation); h.Add(s.preExposure); h.Add(s.hdrMax); h.Add(s.simulateClamp); h.Add(testColor);
            int hash = h.ToHashCode();

            if (!force && hash == m_PreviewHash && m_RampR != null && m_Strip != null)
                return;
            m_PreviewHash = hash;

            // Explicit == null (not ??=) so Unity's destroyed-object check applies.
            if (m_RampR == null) m_RampR = CreatePreviewTexture(k_RampWidth);
            if (m_RampG == null) m_RampG = CreatePreviewTexture(k_RampWidth);
            if (m_RampB == null) m_RampB = CreatePreviewTexture(k_RampWidth);
            if (m_Strip == null) m_Strip = CreatePreviewTexture(k_StripWidth);

            var r = new Color32[k_RampWidth];
            var g = new Color32[k_RampWidth];
            var b = new Color32[k_RampWidth];
            for (int i = 0; i < k_RampWidth; i++)
            {
                float v = Mathf.Pow(2f, Mathf.Lerp(k_RampMinLog2, k_RampMaxLog2, i / (k_RampWidth - 1f)));
                r[i] = ToDisplay(Evaluate(new Vector3(v, 0f, 0f), s));
                g[i] = ToDisplay(Evaluate(new Vector3(0f, v, 0f), s));
                b[i] = ToDisplay(Evaluate(new Vector3(0f, 0f, v), s));
            }
            Upload(m_RampR, r);
            Upload(m_RampG, g);
            Upload(m_RampB, b);

            var strip = new Color32[k_StripWidth];
            for (int i = 0; i < k_StripWidth; i++)
            {
                float scale = Mathf.Pow(2f, Mathf.Lerp(-k_StripStops, k_StripStops, i / (k_StripWidth - 1f)));
                strip[i] = ToDisplay(Evaluate(testColor * scale, s));
            }
            Upload(m_Strip, strip);
        }

        // sRGB texture storing sRGB-encoded values: displays correctly in both linear and gamma projects.
        static Texture2D CreatePreviewTexture(int width) => new Texture2D(width, 1, TextureFormat.RGBA32, false, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };

        static Color32 ToDisplay(Vector3 linear) => new Color(Mathf.Clamp01(linear.x), Mathf.Clamp01(linear.y), Mathf.Clamp01(linear.z), 1f).gamma;

        static void Upload(Texture2D tex, Color32[] pixels)
        {
            tex.SetPixels32(pixels);
            tex.Apply(false);
        }

        static GUIStyle s_LeftMini, s_RightMini;
        static GUIStyle LeftMini() => s_LeftMini ??= new GUIStyle(EditorStyles.centeredGreyMiniLabel) { alignment = TextAnchor.MiddleLeft };
        static GUIStyle RightMini() => s_RightMini ??= new GUIStyle(EditorStyles.centeredGreyMiniLabel) { alignment = TextAnchor.MiddleRight };
    }

    /// <summary>
    /// Reflection access to HDRP's (internal) CustomPostProcessOrdersSettings, so the inspector can check and fix registration.
    /// </summary>
    static class CustomPostProcessRegistry
    {
        static readonly Type s_OrdersType = typeof(HDRenderPipelineAsset).Assembly.GetType("UnityEngine.Rendering.HighDefinition.CustomPostProcessOrdersSettings");

        static object GetOrders()
        {
            if (s_OrdersType == null)
                return null;
            var method = typeof(GraphicsSettings).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == nameof(GraphicsSettings.TryGetRenderPipelineSettings) && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            if (method == null)
                return null;
            var args = new object[1];
            return (bool)method.MakeGenericMethod(s_OrdersType).Invoke(null, args) ? args[0] : null;
        }

        /// <summary>Null when it can't be determined (no HDRP global settings, API changed...).</summary>
        public static bool? IsRegistered(Type type)
        {
            try
            {
                var orders = GetOrders();
                var isRegistered = s_OrdersType?.GetMethod("IsCustomPostProcessRegistered");
                if (orders == null || isRegistered == null)
                    return null;
                return (bool)isRegistered.Invoke(orders, new object[] { type });
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool RegisterAfterPostProcess(Type type)
        {
            try
            {
                var orders = GetOrders();
                var list = s_OrdersType?.GetProperty("afterPostProcessCustomPostProcesses")?.GetValue(orders);
                var add = list?.GetType().GetMethod("Add", new[] { typeof(string) });
                if (add == null)
                    return false;
                add.Invoke(list, new object[] { type.AssemblyQualifiedName });

                var globalSettings = GraphicsSettings.GetSettingsForRenderPipeline<HDRenderPipeline>();
                if (globalSettings != null)
                {
                    EditorUtility.SetDirty(globalSettings);
                    AssetDatabase.SaveAssetIfDirty(globalSettings);
                }
                return IsRegistered(type) == true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return false;
            }
        }
    }
}
