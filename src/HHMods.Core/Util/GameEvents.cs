using System;

namespace HHMods.Core
{
    /// <summary>
    /// Global lifecycle events for the game world. Subscribe from plugin <c>Awake</c>;
    /// handlers fire once the referenced managers exist. Late subscribers get called immediately.
    /// </summary>
    public static class GameEvents
    {
        private static bool _hubReadyFired;
        private static Action _hubReady;

        /// <summary>
        /// Fires once when Mgr_Hub._ins is non-null and its managers have been assigned.
        /// Fires again on scene reload if the hub gets recreated (best-effort).
        /// </summary>
        public static event Action HubReady
        {
            add
            {
                _hubReady += value;
                if (_hubReadyFired) { try { value?.Invoke(); } catch (Exception e) { Plugin.Log.LogError(e); } }
            }
            remove { _hubReady -= value; }
        }

        internal static void Poll()
        {
            if (_hubReadyFired) return;
            var hub = MgrHub.HubOrNull;
            if (hub == null) return;
            // Sanity: at least one downstream manager assigned
            if (hub._SkillMgr == null && hub._PlayerMgr == null) return;

            _hubReadyFired = true;
            Plugin.Log.LogInfo("[GameEvents] Hub ready — firing HubReady");
            try { _hubReady?.Invoke(); }
            catch (Exception e) { Plugin.Log.LogError($"[GameEvents] HubReady handler threw: {e}"); }
        }

        /// <summary>
        /// Reset the fired-flag so HubReady fires again after a scene reload.
        /// Called by save-load hooks (future).
        /// </summary>
        internal static void MarkHubReloaded() => _hubReadyFired = false;
    }
}
