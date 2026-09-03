using UnityEngine;

namespace HHMods.Minimap.V2
{
    /// <summary>Toggles the V2 minimap on the configured hotkey.</summary>
    public class MinimapV2HotkeyBinding : MonoBehaviour
    {
        private void Update()
        {
            if (!Plugin.ToggleKey.Value.IsDown()) return;
            var ctrl = MinimapV2Controller.Instance;
            if (ctrl != null) ctrl.SetVisible(!ctrl.IsMinimapOn);
        }
    }
}
