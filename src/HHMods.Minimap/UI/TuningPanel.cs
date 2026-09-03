using BepInEx.Configuration;
using UnityEngine;

namespace HHMods.Minimap
{
    /// <summary>
    /// IMGUI HUD panel with live sliders/toggles for every tunable minimap ConfigEntry.
    /// Toggled by <see cref="Plugin.TuningPanelKey"/> (default F9). Values here go through
    /// the same ConfigEntry setters that BepInEx.ConfigurationManager would use, so both
    /// this panel and CM stay in sync automatically.
    /// </summary>
    public class TuningPanel : MonoBehaviour
    {
        private bool _visible;
        private Rect _rect = new Rect(20, 200, 380, 560);
        private Vector2 _scroll;
        private static readonly int _windowId = "HHMods.Minimap.TuningPanel".GetHashCode();
        private GUIStyle _sectionStyle;
        private GUIStyle _valueStyle;

        private void Update()
        {
            if (Plugin.TuningPanelKey.Value.IsDown()) _visible = !_visible;
        }

        private void OnGUI()
        {
            if (!_visible) return;

            if (_sectionStyle == null)
            {
                _sectionStyle = new GUIStyle(GUI.skin.label)
                {
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = new Color(1f, 0.85f, 0.30f) },   // amber, matches suite palette
                    padding = new RectOffset(0, 0, 6, 2),
                };
                _valueStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleRight,
                    normal = { textColor = new Color(0.8f, 0.8f, 0.8f) },
                };
            }

            _rect = GUI.Window(_windowId, _rect, DrawWindow, $"Minimap Tuning  ({Plugin.TuningPanelKey.Value})");
        }

        private void DrawWindow(int id)
        {
            _scroll = GUILayout.BeginScrollView(_scroll);

            GUILayout.Label("Rendering", _sectionStyle);
            SliderFloat(Plugin.LightIntensityLux, "Light Intensity (lux)", 0f, 200000f, "0");
            SliderFloat(Plugin.LightAngleX, "Light Pitch (X°)", 0f, 90f, "0");
            SliderFloat(Plugin.LightAngleY, "Light Yaw (Y°)", 0f, 360f, "0");
            SliderFloat(Plugin.FixedExposureEV, "Fixed Exposure (EV)", 0f, 22f, "0.00");
            SliderFloat(Plugin.Saturation, "Saturation", -100f, 100f, "0");

            GUILayout.Label("Player Arrow", _sectionStyle);
            SliderFloat(Plugin.ArrowSize, "Size (px)", 4f, 32f, "0.0");
            SliderFloat(Plugin.ArrowFillAlpha, "Fill Alpha", 0f, 1f, "0.00");
            SliderFloat(Plugin.ArrowOutlineDistance, "Outline (px)", 0f, 5f, "0.0");
            SliderFloat(Plugin.ArrowOutlineAlpha, "Outline Alpha", 0f, 1f, "0.00");

            GUILayout.Label("Camera Culling", _sectionStyle);
            ToggleBool(Plugin.CullTransparentFX, "Cull TransparentFX (layer 1)");
            ToggleBool(Plugin.CullWater,          "Cull Water (layer 4)");
            ToggleBool(Plugin.CullUI,             "Cull UI (layer 5)");

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset to Defaults")) ResetAll();
            if (GUILayout.Button("Close")) _visible = false;
            GUILayout.EndHorizontal();

            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, _rect.width, 20));
        }

        // ---------- Row helpers ----------

        private void SliderFloat(ConfigEntry<float> entry, string label, float min, float max, string fmt)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(150));
            var newVal = GUILayout.HorizontalSlider(entry.Value, min, max, GUILayout.Width(160));
            GUILayout.Label(entry.Value.ToString(fmt), _valueStyle, GUILayout.Width(58));
            GUILayout.EndHorizontal();
            // Only assign back when the slider actually moved — avoids spamming SettingChanged
            // (and its throttling / disk writes) with identical values every OnGUI pass.
            if (!Mathf.Approximately(newVal, entry.Value)) entry.Value = newVal;
        }

        private void ToggleBool(ConfigEntry<bool> entry, string label)
        {
            var newVal = GUILayout.Toggle(entry.Value, "  " + label);
            if (newVal != entry.Value) entry.Value = newVal;
        }

        private static void ResetAll()
        {
            Plugin.LightIntensityLux.Value    = (float)Plugin.LightIntensityLux.DefaultValue;
            Plugin.LightAngleX.Value          = (float)Plugin.LightAngleX.DefaultValue;
            Plugin.LightAngleY.Value          = (float)Plugin.LightAngleY.DefaultValue;
            Plugin.FixedExposureEV.Value      = (float)Plugin.FixedExposureEV.DefaultValue;
            Plugin.Saturation.Value           = (float)Plugin.Saturation.DefaultValue;
            Plugin.ArrowSize.Value            = (float)Plugin.ArrowSize.DefaultValue;
            Plugin.ArrowFillAlpha.Value       = (float)Plugin.ArrowFillAlpha.DefaultValue;
            Plugin.ArrowOutlineDistance.Value = (float)Plugin.ArrowOutlineDistance.DefaultValue;
            Plugin.ArrowOutlineAlpha.Value    = (float)Plugin.ArrowOutlineAlpha.DefaultValue;
            Plugin.CullTransparentFX.Value    = (bool) Plugin.CullTransparentFX.DefaultValue;
            Plugin.CullWater.Value            = (bool) Plugin.CullWater.DefaultValue;
            Plugin.CullUI.Value               = (bool) Plugin.CullUI.DefaultValue;
        }
    }
}
