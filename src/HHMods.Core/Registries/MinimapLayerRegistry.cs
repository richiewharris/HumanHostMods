using System;
using System.Collections.Generic;
using UnityEngine;

namespace HHMods.Core
{
    /// <summary>
    /// A minimap filter layer. Any plugin can register one; the Minimap plugin
    /// subscribes to <see cref="LayerAdded"/> to spawn the actual filter checkbox
    /// and manages POI visibility per layer.
    /// </summary>
    public sealed class MinimapLayer
    {
        /// <summary>Stable id (e.g. "ore", "merchants", "waypoints").</summary>
        public string Id { get; set; }
        /// <summary>Human-readable label shown in the filter panel.</summary>
        public string DisplayName { get; set; }
        /// <summary>Icon sprite used for POIs in this layer (optional).</summary>
        public Sprite Icon { get; set; }
        /// <summary>Optional tint applied to icons in this layer.</summary>
        public Color Color { get; set; } = Color.white;
        /// <summary>Default visibility (before user toggles).</summary>
        public bool DefaultVisible { get; set; } = true;
        /// <summary>
        /// Optional gating predicate — if returns false, the layer's checkbox is shown
        /// but disabled (with a lock icon) and its POIs are hidden. Use this for perk-gated layers.
        /// </summary>
        public Func<bool> IsUnlocked { get; set; } = () => true;
    }

    public static class MinimapLayerRegistry
    {
        private static readonly Dictionary<string, MinimapLayer> _layers = new Dictionary<string, MinimapLayer>();

        /// <summary>Fires when a new layer is registered. Late subscribers get replayed the current set.</summary>
        public static event Action<MinimapLayer> LayerAdded
        {
            add
            {
                _layerAdded += value;
                foreach (var l in _layers.Values)
                {
                    try { value?.Invoke(l); }
                    catch (Exception e) { Plugin.Log.LogError($"[MinimapLayerRegistry] replay handler threw: {e}"); }
                }
            }
            remove { _layerAdded -= value; }
        }
        private static Action<MinimapLayer> _layerAdded;

        /// <summary>Register a filter layer. Safe to call at plugin Awake.</summary>
        public static void Register(MinimapLayer layer)
        {
            if (layer == null) throw new ArgumentNullException(nameof(layer));
            if (string.IsNullOrEmpty(layer.Id)) throw new ArgumentException("MinimapLayer.Id is required.");
            if (_layers.ContainsKey(layer.Id))
            {
                Plugin.Log.LogWarning($"[MinimapLayerRegistry] layer {layer.Id} already registered — replacing.");
            }
            _layers[layer.Id] = layer;
            try { _layerAdded?.Invoke(layer); }
            catch (Exception e) { Plugin.Log.LogError($"[MinimapLayerRegistry] LayerAdded handler threw: {e}"); }
        }

        public static MinimapLayer Get(string id) => _layers.TryGetValue(id, out var l) ? l : null;
        public static IEnumerable<MinimapLayer> All => _layers.Values;
    }
}
