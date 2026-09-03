using System.Collections.Generic;
using UnityEngine;

namespace HHMods.QoL.ChatBox
{
    /// <summary>Tag identifying a message's origin/kind. Determines color + filter chip.</summary>
    public enum NoticeTag
    {
        Info = 0,       // catch-all informational (non-pickup, non-warning)
        Pickup = 1,     // "[ Name ] x Count" from Show_Pickup_Notice
        Warning = 2,    // shield-hint style ("... can only ..." type nudges)
        Broadcast = 3,  // horde broadcasts / world events
    }

    /// <summary>
    /// Persistent notification log. All incoming notifications get a tag and a color;
    /// filter chips at the top let the player show/hide each tag. Newest is at the top.
    /// Buffer holds up to 200 messages; the visible list shows the most recent maxRows
    /// of currently-visible tags. A small A-size picker at the top-right controls the
    /// message-row font size (row text only; chrome ignores it).
    /// </summary>
    public class ChatBox : MonoBehaviour
    {
        public struct Entry { public string Text; public float StampSeconds; public NoticeTag Tag; }

        private const int MaxBuffer = 200;
        private const int TagCount = 4;

        private static ChatBox _instance;
        private readonly LinkedList<Entry> _messages = new LinkedList<Entry>();

        private GUIStyle _rowStyle;
        private GUIStyle _chipLabelStyle;
        private GUIStyle _pickerTriggerStyle;
        private GUIStyle[] _pickerOptionStyles;   // one per size, so each renders "A" at its own scale
        private Texture2D _bgTex;
        private Texture2D _whiteTex;

        // UI state
        private bool _pickerOpen;
        private int _cachedFontSize = -1;         // triggers _rowStyle refresh when config changes

        // Font-size options for the picker (small → large; excludes extremes per user spec).
        private static readonly int[] _fontSizeOptions = new[] { 12, 14, 16, 18 };

        // Static tag colors — used for both the row text and the filter chip highlight.
        private static readonly Color[] _tagColors = new[]
        {
            new Color(0.85f, 0.85f, 0.85f),   // Info    — grey
            new Color(0.55f, 0.90f, 0.45f),   // Pickup  — green
            new Color(0.95f, 0.85f, 0.35f),   // Warning — yellow / amber
            new Color(0.95f, 0.55f, 0.20f),   // Broadcast — orange
        };
        private static readonly string[] _tagLabels = new[] { "Info", "Pickup", "Warn", "Broadcast" };

        /// <summary>Post a message with a tag. Newest goes to the top of the log.</summary>
        public static void Post(string message, NoticeTag tag = NoticeTag.Info)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (_instance == null)
            {
                HHMods.Core.Plugin.Log?.LogWarning($"[ChatBox] Post before spawn; dropped: \"{message}\"");
                return;
            }
            _instance._messages.AddFirst(new Entry { Text = message, StampSeconds = Time.unscaledTime, Tag = tag });
            while (_instance._messages.Count > MaxBuffer)
                _instance._messages.RemoveLast();
            HHMods.Core.Plugin.Log?.LogInfo($"[ChatBox] posted ({tag}): \"{message}\"");
        }

        private void Awake() { _instance = this; }
        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            if (_bgTex != null) Destroy(_bgTex);
            if (_whiteTex != null) Destroy(_whiteTex);
        }

        private void OnGUI()
        {
            if (!Plugin.ChatBoxEnabled.Value) return;
            // Don't render while the game world is loading — HubOrNull returns non-null only
            // once Mgr_Hub has been spawned by the game (i.e., we're in gameplay, not the
            // loading screen or main menu).
            if (HHMods.Core.MgrHub.HubOrNull == null) return;
            // Hide during menus / inventory / any UI-focused mode. Unity's IMGUI layer draws
            // on top of everything (including the game's Canvas UI), and there's no reliable
            // way to z-order IMGUI behind another UI. Instead we suppress the ChatBox when
            // the cursor is unlocked — the game locks the cursor to screen-center during
            // gameplay and releases it for every menu / inventory / map / esc-panel.
            if (Cursor.lockState != CursorLockMode.Locked) return;

            EnsureStyles();
            var w = Plugin.ChatBoxWidth.Value;
            const float chipRowH = 22f;
            const float rowGap = 2f;
            const float rowLeftPad = 8f;
            const float rowRightPad = 8f;
            var textWidth = w - rowLeftPad - rowRightPad;
            var maxRows = Plugin.ChatBoxMaxRows.Value;

            // Pre-pass: pick the visible messages (respecting filters + maxRows) and measure
            // each one's wrapped height. We need the total before positioning the panel bg,
            // since the panel's Y anchor depends on its height.
            var visibleRows = new System.Collections.Generic.List<(GUIContent content, Color color, float height)>();
            float totalRowsH = 0f;
            int taken = 0;
            for (var node = _messages.First; node != null && taken < maxRows; node = node.Next)
            {
                if (!IsTagEnabled(node.Value.Tag)) continue;
                var content = new GUIContent(node.Value.Text);
                var lineH = _rowStyle.CalcHeight(content, textWidth);
                visibleRows.Add((content, _tagColors[(int)node.Value.Tag], lineH));
                totalRowsH += lineH + rowGap;
                taken++;
            }

            var h = 4f + chipRowH + 2f + Mathf.Max(20f, totalRowsH) + 4f;

            var marginX = Plugin.ChatBoxMarginX.Value;
            var marginY = Plugin.ChatBoxMarginY.Value;
            var anchor = Plugin.ChatBoxAnchor01.Value;
            var x = Mathf.Lerp(marginX, Screen.width  - w - marginX, anchor.x);
            var y = Mathf.Lerp(Screen.height - h - marginY, marginY, anchor.y);

            // Background panel
            GUI.DrawTexture(new Rect(x, y, w, h), _bgTex, ScaleMode.StretchToFill);

            // -------- Font-size picker (top-right) --------
            // Reserve the trigger's rect first so the filter chips can size themselves against it.
            const float pickerW = 42f;
            var pickerH = chipRowH - 4f;
            var pickerX = x + w - pickerW - 4f;
            var pickerY = y + 3f;
            var pickerRect = new Rect(pickerX, pickerY, pickerW, pickerH);
            var pickerAreaRight = pickerX - 4f;   // filter chips must stay left of this

            // -------- Filter chips — flat matte tiles, always in tag color, 40% alpha off --------
            const float chipGap = 4f;
            var chipsAvailable = pickerAreaRight - (x + 4f);
            var chipW = Mathf.Min(80f, (chipsAvailable - (TagCount - 1) * chipGap) / TagCount);
            for (int i = 0; i < TagCount; i++)
            {
                var cx = x + 4f + i * (chipW + chipGap);
                var rect = new Rect(cx, y + 3f, chipW, chipRowH - 4f);
                DrawFlatChip(rect, (NoticeTag)i);
            }

            // -------- Picker trigger (rendered after chips so it wins z-order for the click test) --------
            DrawFontSizePicker(pickerRect);

            // -------- Message rows (top = newest, wrapped, filtered by tag) --------
            var contentY = y + chipRowH + 2f;
            var rowY = contentY;
            foreach (var (content, color, lineH) in visibleRows)
            {
                var orig = _rowStyle.normal.textColor;
                _rowStyle.normal.textColor = color;
                GUI.Label(new Rect(x + rowLeftPad, rowY, textWidth, lineH), content, _rowStyle);
                _rowStyle.normal.textColor = orig;
                rowY += lineH + rowGap;
            }

            // -------- Dropdown option list (rendered LAST so it paints over everything, including
            // the rows underneath) --------
            if (_pickerOpen)
                DrawFontSizePickerOptions(pickerRect);

            // Close the dropdown if the player clicks outside it.
            if (_pickerOpen && Event.current.type == EventType.MouseDown)
            {
                var listRect = new Rect(pickerX, pickerY + pickerH, pickerW, _fontSizeOptions.Length * pickerH);
                if (!pickerRect.Contains(Event.current.mousePosition) && !listRect.Contains(Event.current.mousePosition))
                    _pickerOpen = false;
            }
        }

        /// <summary>Draws one filter chip as a flat colored tile with the tag label centered on top.</summary>
        private void DrawFlatChip(Rect rect, NoticeTag tag)
        {
            var enabled = IsTagEnabled(tag);
            var baseColor = MatteVersion(_tagColors[(int)tag]);
            var fill = baseColor;
            fill.a = enabled ? 0.95f : (0.95f * 0.60f);   // 40% opacity reduction when disabled

            // Colored tile
            var prevColor = GUI.color;
            GUI.color = fill;
            GUI.DrawTexture(rect, _whiteTex);
            GUI.color = prevColor;

            // Label — dark on enabled color, softer dark on disabled (readable on both)
            var origText = _chipLabelStyle.normal.textColor;
            _chipLabelStyle.normal.textColor = enabled
                ? new Color(0.10f, 0.08f, 0.06f, 1f)
                : new Color(0.10f, 0.08f, 0.06f, 0.75f);
            GUI.Label(rect, _tagLabels[(int)tag], _chipLabelStyle);
            _chipLabelStyle.normal.textColor = origText;

            // Click detection — use the rect directly so we don't inherit any button-skin
            // rendering that would render behind the panel bg.
            if (Event.current.type == EventType.MouseUp && rect.Contains(Event.current.mousePosition))
            {
                SetTagEnabled(tag, !enabled);
                Event.current.Use();
            }
        }

        private void DrawFontSizePicker(Rect trigger)
        {
            // Trigger: a small tile showing "A ▾" at a moderate size, matches chip flatness.
            var prevColor = GUI.color;
            GUI.color = new Color(0.15f, 0.13f, 0.10f, 0.9f);
            GUI.DrawTexture(trigger, _whiteTex);
            GUI.color = prevColor;
            GUI.Label(trigger, "A ▾", _pickerTriggerStyle);

            if (Event.current.type == EventType.MouseUp && trigger.Contains(Event.current.mousePosition))
            {
                _pickerOpen = !_pickerOpen;
                Event.current.Use();
            }
        }

        private void DrawFontSizePickerOptions(Rect trigger)
        {
            var current = Plugin.ChatBoxFontSize.Value;
            for (int i = 0; i < _fontSizeOptions.Length; i++)
            {
                var rect = new Rect(trigger.x, trigger.yMax + i * trigger.height, trigger.width, trigger.height);
                var selected = _fontSizeOptions[i] == current;

                var prev = GUI.color;
                GUI.color = selected ? new Color(0.28f, 0.26f, 0.22f, 1f) : new Color(0.15f, 0.13f, 0.10f, 0.98f);
                GUI.DrawTexture(rect, _whiteTex);
                GUI.color = prev;

                GUI.Label(rect, "A", _pickerOptionStyles[i]);

                if (Event.current.type == EventType.MouseUp && rect.Contains(Event.current.mousePosition))
                {
                    Plugin.ChatBoxFontSize.Value = _fontSizeOptions[i];
                    _pickerOpen = false;
                    _cachedFontSize = -1;   // force _rowStyle rebuild next frame
                    Event.current.Use();
                }
            }
        }

        /// <summary>Mixes a saturated color with neutral grey for a matte, less-web-2.0 look.</summary>
        private static Color MatteVersion(Color c)
        {
            // Blend 30% toward mid-grey.
            return Color.Lerp(c, new Color(0.55f, 0.53f, 0.50f), 0.30f);
        }

        private static bool IsTagEnabled(NoticeTag tag)
        {
            switch (tag)
            {
                case NoticeTag.Info:      return Plugin.ShowInfo.Value;
                case NoticeTag.Pickup:    return Plugin.ShowPickup.Value;
                case NoticeTag.Warning:   return Plugin.ShowWarning.Value;
                case NoticeTag.Broadcast: return Plugin.ShowBroadcast.Value;
                default: return true;
            }
        }
        private static void SetTagEnabled(NoticeTag tag, bool value)
        {
            switch (tag)
            {
                case NoticeTag.Info:      Plugin.ShowInfo.Value = value; break;
                case NoticeTag.Pickup:    Plugin.ShowPickup.Value = value; break;
                case NoticeTag.Warning:   Plugin.ShowWarning.Value = value; break;
                case NoticeTag.Broadcast: Plugin.ShowBroadcast.Value = value; break;
            }
        }

        private void EnsureStyles()
        {
            if (_bgTex == null)
            {
                _bgTex = new Texture2D(1, 1);
                _bgTex.SetPixel(0, 0, new Color(0.05f, 0.04f, 0.03f, 0.78f));
                _bgTex.Apply();
                _bgTex.hideFlags = HideFlags.HideAndDontSave;
            }
            if (_whiteTex == null)
            {
                _whiteTex = new Texture2D(1, 1);
                _whiteTex.SetPixel(0, 0, Color.white);
                _whiteTex.Apply();
                _whiteTex.hideFlags = HideFlags.HideAndDontSave;
            }
            // Row style depends on the configured font size — rebuild when the config changes.
            if (_rowStyle == null || _cachedFontSize != Plugin.ChatBoxFontSize.Value)
            {
                _cachedFontSize = Plugin.ChatBoxFontSize.Value;
                _rowStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = _cachedFontSize,
                    alignment = TextAnchor.UpperLeft,   // wrapped multi-line reads better top-aligned
                    wordWrap = true,
                    clipping = TextClipping.Clip,
                };
            }
            if (_chipLabelStyle == null)
            {
                _chipLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 11,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = new Color(0.10f, 0.08f, 0.06f, 1f) },
                };
                _pickerTriggerStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = new Color(0.90f, 0.86f, 0.78f, 1f) },
                };
                _pickerOptionStyles = new GUIStyle[_fontSizeOptions.Length];
                for (int i = 0; i < _fontSizeOptions.Length; i++)
                {
                    _pickerOptionStyles[i] = new GUIStyle(GUI.skin.label)
                    {
                        fontSize = _fontSizeOptions[i],
                        fontStyle = FontStyle.Bold,
                        alignment = TextAnchor.MiddleCenter,
                        normal = { textColor = new Color(0.90f, 0.86f, 0.78f, 1f) },
                    };
                }
            }
        }
    }
}
