using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.Rendering.HighDefinition;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace AgX.Editors
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(AgxTonemapping))]
    sealed class AgxTonemappingEditor : CustomPostProcessVolumeComponentEditor
    {
        // Ramp range: 2^-8 (0.004) to 2^8 (256), log2 scale.
        const float k_RampMinLog2 = -8f;
        const float k_RampMaxLog2 = 8f;
        const int k_RampWidth = 512;

        SerializedDataParameter m_Mode, m_Exposure, m_Look, m_Slope, m_Offset, m_Power, m_Saturation;

        Texture2D m_RampR, m_RampG, m_RampB;
        int m_PreviewHash;
        readonly Vector3[] m_CurvePoints = new Vector3[160];

        public override void OnEnable()
        {
            base.OnEnable();
            var o = new PropertyFetcher<AgxTonemapping>(serializedObject);
            m_Mode = Unpack(o.Find(x => x.mode));
            m_Exposure = Unpack(o.Find(x => x.exposure));
            m_Look = Unpack(o.Find(x => x.look));
            m_Slope = Unpack(o.Find(x => x.slope));
            m_Offset = Unpack(o.Find(x => x.offset));
            m_Power = Unpack(o.Find(x => x.power));
            m_Saturation = Unpack(o.Find(x => x.saturation));
        }

        public override void OnDisable()
        {
            base.OnDisable();
            CoreUtils.Destroy(m_RampR);
            CoreUtils.Destroy(m_RampG);
            CoreUtils.Destroy(m_RampB);
            m_RampR = m_RampG = m_RampB = null;
        }

        public override void OnInspectorGUI()
        {
            DrawRegistrationBox();
            DrawHdrpTonemappingBox();

            PropertyField(m_Mode, Label(m_Mode, "Mode"));
            PropertyField(m_Exposure, Label(m_Exposure, "Exposure"));
            PropertyField(m_Look, Label(m_Look, "Look"));
            if (m_Look.value.intValue == (int)AgxLook.Custom)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    PropertyField(m_Slope, Label(m_Slope, "Slope"));
                    PropertyField(m_Offset, Label(m_Offset, "Offset"));
                    PropertyField(m_Power, Label(m_Power, "Power"));
                    PropertyField(m_Saturation, Label(m_Saturation, "Saturation"));
                }
            }

            var s = ReadSettings();
            DrawInfo(s);
            DrawToneCurve(s);
            DrawHdrRamps(s);
        }

        static GUIContent Label(SerializedDataParameter p, string text)
            => new GUIContent(text, p.GetAttribute<TooltipAttribute>()?.tooltip);

        // ---------------------------------------------------------------------------------------------------------------
        // Settings snapshot (from the serialized values so previews update while dragging).

        struct Settings
        {
            public AgxCdl look;
            public float preExposure;
        }

        Settings ReadSettings() => new Settings
        {
            look = AgxTonemapping.GetLook((AgxLook)m_Look.value.intValue, m_Slope.value.vector3Value, m_Offset.value.vector3Value,
                m_Power.value.vector3Value, m_Saturation.value.floatValue),
            preExposure = Mathf.Pow(2f, m_Exposure.value.floatValue),
        };

        static Vector3 Evaluate(Vector3 sceneLinear, in Settings s) => AgxCore.Map(sceneLinear * s.preExposure, s.look);

        // ---------------------------------------------------------------------------------------------------------------
        // Setup checks

        void DrawRegistrationBox()
        {
            bool? registered = AgxPostProcessRegistry.IsRegistered(typeof(AgxTonemapping));
            if (registered != false)
                return;

            EditorGUILayout.HelpBox("This custom post process is not registered in Project Settings > Graphics > HDRP Global Settings > Custom Post Process Orders, so HDRP will not render it.", MessageType.Error);
            if (GUILayout.Button("Register in \"After Post Process\"") && !AgxPostProcessRegistry.RegisterAfterPostProcess(typeof(AgxTonemapping)))
                Debug.LogError("AgX: could not register the custom post process automatically. Add it manually in Project Settings > Graphics > HDRP Global Settings > Custom Post Process Orders > After Post Process.");
            EditorGUILayout.Space();
        }

        void DrawHdrpTonemappingBox()
        {
            if (m_Mode.value.intValue != (int)AgxTonemappingMode.AgX)
                return;

            var stack = VolumeManager.instance.isInitialized ? VolumeManager.instance.stack : null;
            var hdrpTonemapping = stack?.GetComponent<Tonemapping>();
            if (hdrpTonemapping == null || !hdrpTonemapping.IsActive())
                return;

            EditorGUILayout.HelpBox($"HDRP Tonemapping is also active ({hdrpTonemapping.mode.value}), so the image gets tonemapped twice. Set its Mode to None; AgX replaces it.", MessageType.Warning);

            string path = AssetDatabase.GetAssetPath(target);
            var profile = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadMainAssetAtPath(path) as VolumeProfile;
            if (profile != null && GUILayout.Button("Set HDRP Tonemapping to None in this profile"))
                OverrideHdrpTonemappingToNone(profile);
            EditorGUILayout.Space();
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
        // Info

        void DrawInfo(in Settings s)
        {
            float mid = Mathf.LinearToGammaSpace(Evaluate(Vector3.one * 0.18f, s).y);
            float whiteAt = Mathf.Pow(2f, AgxCore.MaxEv) / s.preExposure;
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(
                new GUIContent($"Scene 0.18 → {mid:0.000} on screen (sRGB) · encoding tops out at {whiteAt:0.#}",
                    "AgX encodes 10 stops below to 6.5 stops above mid-gray (after Exposure). Above that the input is clipped to the top of the curve."),
                EditorStyles.miniLabel);
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
            EditorGUI.DrawRect(new Rect(X(0f), rect.yMin, 1f, rect.height), pro ? new Color(1f, 1f, 1f, 0.25f) : new Color(0f, 0f, 0f, 0.3f));

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

            GUI.Label(new Rect(labels.xMin, labels.y, 60f, labels.height), $"{minEv:+0;-0} EV", LeftMini());
            GUI.Label(new Rect(X(0f) - 40f, labels.y, 80f, labels.height), "mid-gray", EditorStyles.centeredGreyMiniLabel);
            GUI.Label(new Rect(labels.xMax - 60f, labels.y, 60f, labels.height), $"{maxEv:+0;-0} EV", RightMini());
        }

        // ---------------------------------------------------------------------------------------------------------------
        // HDR ramps of pure primaries

        void DrawHdrRamps(in Settings s)
        {
            UpdateRamps(s);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("HDR color preview (pure RGB)", EditorStyles.boldLabel);
            Rect first = default;
            DrawRampRow("R", m_RampR, ref first);
            DrawRampRow("G", m_RampG, ref first);
            DrawRampRow("B", m_RampB, ref first);

            Rect axis = EditorGUI.IndentedRect(GUILayoutUtility.GetRect(10f, 14f, GUILayout.ExpandWidth(true)));
            axis.xMin = first.xMin;
            float Ax(float log2) => Mathf.Lerp(axis.xMin, axis.xMax, Mathf.InverseLerp(k_RampMinLog2, k_RampMaxLog2, log2));
            GUI.Label(new Rect(axis.xMin, axis.y, 50f, axis.height), "0.004", LeftMini());
            GUI.Label(new Rect(Ax(0f) - 25f, axis.y, 50f, axis.height), "1", EditorStyles.centeredGreyMiniLabel);
            GUI.Label(new Rect(axis.xMax - 50f, axis.y, 50f, axis.height), "256", RightMini());
            EditorGUILayout.LabelField("Linear input intensity, logarithmic scale", EditorStyles.centeredGreyMiniLabel);
        }

        static void DrawRampRow(string label, Texture2D tex, ref Rect first)
        {
            Rect row = EditorGUI.IndentedRect(GUILayoutUtility.GetRect(10f, 20f, GUILayout.ExpandWidth(true)));
            var rampRect = new Rect(row.x + 18f, row.y + 1f, row.width - 18f, row.height - 2f);
            GUI.Label(new Rect(row.x, row.y, 16f, row.height), label, EditorStyles.miniLabel);
            if (Event.current.type == EventType.Repaint && tex != null)
                GUI.DrawTexture(rampRect, tex, ScaleMode.StretchToFill, false);
            if (first == default) first = rampRect;
        }

        void UpdateRamps(in Settings s)
        {
            var h = new HashCode();
            h.Add(s.look.slope); h.Add(s.look.offset); h.Add(s.look.power); h.Add(s.look.saturation); h.Add(s.preExposure);
            int hash = h.ToHashCode();
            if (hash == m_PreviewHash && m_RampR != null)
                return;
            m_PreviewHash = hash;

            if (m_RampR == null) m_RampR = CreateRamp();
            if (m_RampG == null) m_RampG = CreateRamp();
            if (m_RampB == null) m_RampB = CreateRamp();

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
        }

        // sRGB-encoded values in an RGBA32 texture: displays correctly in both linear and gamma projects.
        static Texture2D CreateRamp() => new Texture2D(k_RampWidth, 1, TextureFormat.RGBA32, false, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };

        static Color32 ToDisplay(Vector3 linear) => new Color(linear.x, linear.y, linear.z, 1f).gamma;

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
    static class AgxPostProcessRegistry
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
