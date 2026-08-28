using System.Collections.Generic;
using HHMods.Core;
using UnityEngine;

namespace HHMods.Minimap
{
    /// <summary>
    /// Minimal IMGUI-based filter panel. Renders one toggle per registered layer.
    /// Gated layers (Geologist etc.) show as disabled with a padlock prefix.
    ///
    /// v1 is deliberately IMGUI so we don't take a hard dependency on the game's
    /// DuloGames UI framework before we've validated the minimap end-to-end.
    /// Wave-1.5 replaces this with a proper Canvas-based panel.
    /// </summary>
    public class FilterPanel : MonoBehaviour
    {
        private bool _visible;
        private readonly Dictionary<string, bool> _userToggles = new Dictionary<string, bool>();
        private Rect _windowRect = new Rect(20, 20, 200, 40);
        private static readonly int _windowId = "HHMods.Minimap.FilterPanel".GetHashCode();

        private void OnEnable()
        {
            MinimapLayerRegistry.LayerAdded += OnLayerAdded;
        }

        private void OnDisable()
        {
            MinimapLayerRegistry.LayerAdded -= OnLayerAdded;
        }

        internal void SetVisible(bool visible) => _visible = visible;

        private void OnLayerAdded(MinimapLayer layer)
        {
            if (!_userToggles.ContainsKey(layer.Id))
                _userToggles[layer.Id] = layer.DefaultVisible;
        }

        private void OnGUI()
        {
            if (!_visible) return;

            // Anchor the panel to the same corner as the minimap (top-right by default).
            var anchor = Plugin.ScreenAnchor01.Value;
            var size = Plugin.Size.Value;
            var x = Mathf.Lerp(0, Screen.width - _windowRect.width - 8, anchor.x);
            var y = Mathf.Lerp(0, Screen.height - _windowRect.height - 8, anchor.y) + size + 8;
            _windowRect.position = new Vector2(x, y);

            _windowRect = GUI.Window(_windowId, _windowRect, DrawWindow, "Minimap Filters");
        }

        private void DrawWindow(int id)
        {
            var layers = new List<MinimapLayer>(MinimapLayerRegistry.All);
            var lineHeight = 22f;
            _windowRect.height = 24f + layers.Count * lineHeight + 8f;

            var y = 24f;
            foreach (var layer in layers)
            {
                if (!_userToggles.TryGetValue(layer.Id, out var toggled)) toggled = layer.DefaultVisible;
                var unlocked = layer.IsUnlocked == null || layer.IsUnlocked();
                var label = unlocked ? layer.DisplayName : $"🔒 {layer.DisplayName}";

                GUI.enabled = unlocked;
                var newVal = GUI.Toggle(new Rect(10, y, _windowRect.width - 20, lineHeight - 2), toggled, label);
                GUI.enabled = true;

                if (newVal != toggled) _userToggles[layer.Id] = newVal;
                y += lineHeight;
            }

            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 20));
        }

        internal bool IsLayerVisible(string layerId)
        {
            if (!_userToggles.TryGetValue(layerId, out var t)) return true;
            var layer = MinimapLayerRegistry.Get(layerId);
            if (layer?.IsUnlocked != null && !layer.IsUnlocked()) return false;
            return t;
        }
    }
}
