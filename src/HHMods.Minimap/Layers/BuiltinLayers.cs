using HHMods.Core;
using UnityEngine;

namespace HHMods.Minimap
{
    /// <summary>
    /// Registers the layers HHMods.Minimap ships with. Other plugins (Perks, Vehicles, etc.)
    /// register their own layers via <see cref="MinimapLayerRegistry.Register"/>.
    /// </summary>
    internal static class BuiltinLayers
    {
        internal const string PlayerWaypointsId = "waypoints";
        internal const string MerchantsId = "merchants";

        public static void RegisterAll()
        {
            MinimapLayerRegistry.Register(new MinimapLayer
            {
                Id = PlayerWaypointsId,
                DisplayName = "Player Waypoints",
                Color = new Color(1f, 0.85f, 0.30f), // amber, matches game's ModBrowser palette
                DefaultVisible = true,
            });

            MinimapLayerRegistry.Register(new MinimapLayer
            {
                Id = MerchantsId,
                DisplayName = "Merchants",
                Color = new Color(0.30f, 0.70f, 1.0f),
                DefaultVisible = true,
            });
        }
    }
}
