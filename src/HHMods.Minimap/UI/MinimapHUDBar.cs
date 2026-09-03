using HHMods.Core;
using UnityEngine;

namespace HHMods.Minimap
{
    /// <summary>
    /// Two HUD strips flanking the minimap: a coords bar ABOVE (X, Z, height) and a biome
    /// nameplate BELOW styled after the minimap bezel (dark chrome fill, cream border,
    /// brass diamond terminators). Both are positioned relative to the live minimap chrome
    /// rect so they follow the map if it moves or resizes. The biome nameplate only appears
    /// while the minimap itself is visible.
    /// </summary>
    public class MinimapHUDBar : MonoBehaviour
    {
        private GUIStyle _coordStyle;
        private GUIStyle _biomeStyle;
        private Texture2D _coordBgTex;
        private Texture2D _bezelBorderTex;
        private Texture2D _bezelFillTex;
        private Texture2D _diamondTex;
        private string _cachedBiome = "";
        private float _biomeNextRefresh;

        private void OnGUI()
        {
            EnsureStyles();

            var mapSize = Plugin.Size.Value;
            const float coordBarH = 26f;
            const float biomeBarH = 26f;
            const float coordBarW = 180f;
            const float ScreenMargin = 8f;

            // Try to position the bars against the live minimap chrome rect. If the minimap
            // hasn't rendered yet, fall back to the top-right screen anchor so the coord bar
            // still shows during load. Biome nameplate is skipped in that case (rule: only
            // show when the map is up).
            Rect chromeRect = default;
            var mapKnown = false;
            var v2 = V2.MinimapV2Controller.Instance;
            if (v2 != null) mapKnown = v2.TryGetChromeScreenRect(out chromeRect);

            float mapCenterX, chromeBottomY;
            if (mapKnown)
            {
                mapCenterX = chromeRect.center.x;
                chromeBottomY = chromeRect.yMax;
            }
            else
            {
                // Fallback for loading screens: point where the map WILL live.
                var rightPad = ScreenMargin + 20f;   // 20 = frameThickness
                mapCenterX = Screen.width - rightPad - mapSize * 0.5f;
                chromeBottomY = 0f;                  // unused (biome bar suppressed)
            }

            // -------- Coord bar (above the minimap, always shown) --------
            var coordX = mapCenterX - coordBarW * 0.5f;
            var coordY = ScreenMargin;
            GUI.DrawTexture(new Rect(coordX, coordY, coordBarW, coordBarH), _coordBgTex, ScaleMode.StretchToFill);

            var pos = ResolveDisplayPosition();
            GUI.Label(new Rect(coordX + 6, coordY + 3, coordBarW - 12, coordBarH - 6),
                      $"( {pos.x:0} , {pos.z:0} )   H {pos.y:0}", _coordStyle);

            // -------- Biome nameplate (below the minimap chrome, only while map is visible) --------
            if (!mapKnown) return;

            if (Time.unscaledTime >= _biomeNextRefresh)
            {
                _biomeNextRefresh = Time.unscaledTime + 1f;
                _cachedBiome = ResolveBiomeName();
            }

            var biomeBarW = mapSize;
            var biomeBarX = mapCenterX - biomeBarW * 0.5f;
            var biomeBarY = chromeBottomY + 6f;

            GUI.DrawTexture(new Rect(biomeBarX, biomeBarY, biomeBarW, biomeBarH), _bezelBorderTex, ScaleMode.StretchToFill);
            GUI.DrawTexture(new Rect(biomeBarX + 2f, biomeBarY + 2f, biomeBarW - 4f, biomeBarH - 4f), _bezelFillTex, ScaleMode.StretchToFill);

            const float diamondW = 12f;
            const float diamondH = 14f;
            var diamondY = biomeBarY + (biomeBarH - diamondH) * 0.5f;
            GUI.DrawTexture(new Rect(biomeBarX + 10f,                        diamondY, diamondW, diamondH), _diamondTex, ScaleMode.StretchToFill);
            GUI.DrawTexture(new Rect(biomeBarX + biomeBarW - 10f - diamondW, diamondY, diamondW, diamondH), _diamondTex, ScaleMode.StretchToFill);

            GUI.Label(new Rect(biomeBarX, biomeBarY, biomeBarW, biomeBarH), _cachedBiome, _biomeStyle);
        }

        private static Vector3 ResolveDisplayPosition()
        {
            var v2 = V2.MinimapV2Controller.Instance;
            if (v2 != null && v2.CurrentFollowPosition != Vector3.zero) return v2.CurrentFollowPosition;
            var v1 = MinimapController.Instance;
            if (v1 != null && v1.CurrentFollowPosition != Vector3.zero) return v1.CurrentFollowPosition;
            var cam = Camera.main;
            if (cam == null)
            {
                var camc = MgrHub.Cam;
                if (camc != null && camc._mainCam != null) cam = camc._mainCam;
            }
            return cam != null ? cam.transform.position : Vector3.zero;
        }

        /// <summary>
        /// Reads the current biome from Weather_Controller._currentWeatherZone, falling back to
        /// the underfoot zone. GameObject names come as "zb_desert" / "Local_Weather_Zone_Forest"
        /// style raw identifiers; we prettify to "Desert" / "Forest" for display.
        /// </summary>
        private static string ResolveBiomeName()
        {
            try
            {
                var wc = global::Weather_Controller.ins;
                if (wc == null) return "";
                var zone = wc._currentWeatherZone ?? wc._underFeetWeatherZone;
                if (zone == null) return "";
                return PrettifyZoneName(zone.gameObject.name);
            }
            catch { return ""; }
        }

        private static string PrettifyZoneName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var s = raw;
            var prefixes = new[] { "zb_", "Local_Weather_Zone_", "Weather_Zone_", "LocalWeatherZone_", "WeatherZone_" };
            foreach (var p in prefixes)
                if (s.StartsWith(p, System.StringComparison.OrdinalIgnoreCase)) { s = s.Substring(p.Length); break; }
            s = s.Replace('_', ' ').Trim();
            if (s.Length == 0) return "";
            return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLower());
        }

        private void EnsureStyles()
        {
            if (_coordBgTex == null)  _coordBgTex     = MakeSolidTex(new Color(0.08f, 0.06f, 0.04f, 0.85f));
            if (_bezelBorderTex == null) _bezelBorderTex = MakeSolidTex(new Color(0.44f, 0.40f, 0.32f, 1f));
            if (_bezelFillTex == null)   _bezelFillTex   = MakeSolidTex(new Color(0.17f, 0.16f, 0.14f, 0.95f));
            if (_diamondTex == null)     _diamondTex     = MakeDiamondTex(24, 28, new Color(0.83f, 0.74f, 0.56f, 1f));

            if (_coordStyle == null)
            {
                _coordStyle = new GUIStyle(GUI.skin.label)
                {
                    fontStyle = FontStyle.Bold,
                    fontSize = 14,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = new Color(0.95f, 0.85f, 0.4f) },
                };
                _biomeStyle = new GUIStyle(GUI.skin.label)
                {
                    fontStyle = FontStyle.Bold,
                    fontSize = 14,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = new Color(0.94f, 0.91f, 0.85f) },
                };
            }
        }

        private static Texture2D MakeSolidTex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        /// <summary>Procedural diamond alpha-mask, rasterized as |dx|/halfW + |dy|/halfH ≤ 1.</summary>
        private static Texture2D MakeDiamondTex(int width, int height, Color color)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.filterMode = FilterMode.Bilinear;
            var cx = width * 0.5f;
            var cy = height * 0.5f;
            var halfW = (width - 2) * 0.5f;
            var halfH = (height - 2) * 0.5f;
            var clear = new Color(0f, 0f, 0f, 0f);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var dx = Mathf.Abs(x + 0.5f - cx) / halfW;
                    var dy = Mathf.Abs(y + 0.5f - cy) / halfH;
                    tex.SetPixel(x, y, (dx + dy) <= 1f ? color : clear);
                }
            }
            tex.Apply();
            return tex;
        }

        private void OnDestroy()
        {
            if (_coordBgTex != null) Destroy(_coordBgTex);
            if (_bezelBorderTex != null) Destroy(_bezelBorderTex);
            if (_bezelFillTex != null) Destroy(_bezelFillTex);
            if (_diamondTex != null) Destroy(_diamondTex);
        }
    }
}
