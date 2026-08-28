using UnityEngine;

namespace HHMods.Minimap
{
    /// <summary>Listens for the configured toggle hotkey; flips the minimap AND filter panel together.</summary>
    public class HotkeyBinding : MonoBehaviour
    {
        private bool _visible;
        private float _nextAliveLog;

        private void Awake()
        {
            Plugin.Log.LogInfo("[HotkeyBinding] Awake");
        }

        private void Start()
        {
            _visible = Plugin.ShowOnStart.Value;
            Plugin.Log.LogInfo($"[HotkeyBinding] Start — listening for {Plugin.ToggleKey.Value}   initial visible={_visible}");
            _nextAliveLog = Time.unscaledTime + 10f;
        }

        private void OnEnable()  => Plugin.Log.LogInfo("[HotkeyBinding] OnEnable");
        private void OnDisable() => Plugin.Log.LogInfo("[HotkeyBinding] OnDisable");

        private void Update()
        {
            // Beacon so we can prove Update is running even if no key is pressed
            if (Time.unscaledTime >= _nextAliveLog)
            {
                _nextAliveLog = Time.unscaledTime + 10f;
                Plugin.Log.LogInfo($"[HotkeyBinding] alive — listening for {Plugin.ToggleKey.Value}   anyKeyDown={Input.anyKeyDown}");
            }

            // Log any N press independently of the KeyboardShortcut check, so we can tell
            // whether input is reaching us at all vs. whether the shortcut match is off.
            if (Input.GetKeyDown(KeyCode.N))
            {
                Plugin.Log.LogInfo("[HotkeyBinding] raw KeyCode.N GetKeyDown detected");
            }

            if (!Plugin.ToggleKey.Value.IsDown()) return;

            _visible = !_visible;
            var ctrl = MinimapController.Instance;
            if (ctrl != null) ctrl.SetVisible(_visible);
            var panel = GetComponent<FilterPanel>();
            if (panel != null) panel.SetVisible(_visible);
            Plugin.Log.LogInfo($"[Minimap] toggle → visible={_visible}");
        }
    }
}
