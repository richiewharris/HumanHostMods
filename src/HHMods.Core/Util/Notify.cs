namespace HHMods.Core
{
    /// <summary>
    /// Best-effort wrapper around the game's NotificationSystem. Silently no-ops
    /// if the notification system isn't yet in the scene.
    /// </summary>
    public static class Notify
    {
        public static void Show(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            var ns = MgrHub.Notifications;
            if (ns == null)
            {
                Plugin.Log.LogInfo($"[Notify → console] {message}");
                return;
            }
            // TODO Wave 1: call the actual NotificationSystem entry point once we've
            //     dumped its API. Until then, log; behavior is uniform per-plugin.
            Plugin.Log.LogInfo($"[Notify] {message}");
        }
    }
}
