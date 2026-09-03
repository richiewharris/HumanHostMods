using CompassNavigatorPro;
using UnityEngine;

namespace HHMods.Minimap
{
    public sealed class MinimapViewPreset
    {
        public string Name;
        public float HeightVSFollow;
        public float CaptureSize;
        public float MinAltitude;
        public float MaxAltitude;
        public MINIMAP_CAMERA_MODE CameraMode;
        public float CameraTilt;
        public MINIMAP_STYLE Style;
        public string Notes;
    }

    /// <summary>
    /// The single validated view. Was previously a cycleable list; consolidated after
    /// in-game testing settled on this configuration as the good default.
    /// </summary>
    internal static class ViewPresets
    {
        public static readonly MinimapViewPreset[] All = new[]
        {
            new MinimapViewPreset {
                Name = "DEFAULT",
                HeightVSFollow = 260f, CaptureSize = 260f, MinAltitude = 150f, MaxAltitude = 1200f,
                CameraMode = MINIMAP_CAMERA_MODE.Orthographic, CameraTilt = 0f,
                Style = MINIMAP_STYLE.SolidCircle,
                Notes = "Ortho top-down, wide radius — validated in-game."
            },
        };
    }
}
