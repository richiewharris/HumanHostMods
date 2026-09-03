using HarmonyLib;
using HHMods.Core;
using UnityEngine;

namespace HHMods.QoL.ChatBox
{
    /// <summary>
    /// Polls <c>NPC_Horde_Mgr._NextHordeText.text</c> once per second. When the text
    /// changes, forwards to the ChatBox as a Broadcast. Optionally hides the game's
    /// own top-right banner so the ChatBox is the single source of truth.
    ///
    /// The horde broadcast doesn't route through NotificationSystem.Add_Notice — the
    /// coroutine writes directly to a persistent UI.Text element via .set_text. So a
    /// Harmony patch alone can't catch it; polling is the reliable path.
    /// </summary>
    public class HordeBroadcastPoller : MonoBehaviour
    {
        private const float PollInterval = 1f;
        private float _nextPoll;
        private string _lastText = "";
        private bool _bannerHidden;

        // _NextHordeText is private on NPC_Horde_Mgr. Reflected once, cached.
        private static System.Reflection.FieldInfo _nextHordeTextField;
        private static UnityEngine.UI.Text ResolveNextHordeText(global::NPC_Horde_Mgr mgr)
        {
            if (mgr == null) return null;
            if (_nextHordeTextField == null)
                _nextHordeTextField = AccessTools.Field(typeof(global::NPC_Horde_Mgr), "_NextHordeText");
            return _nextHordeTextField?.GetValue(mgr) as UnityEngine.UI.Text;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + PollInterval;

            var hordeMgr = MgrHub.HubOrNull?._NPCHordeMgr;
            if (hordeMgr == null) return;
            var textComp = ResolveNextHordeText(hordeMgr);
            if (textComp == null) return;

            // Suppress the on-screen banner once we have a handle to it.
            if (Plugin.SuppressGameHordeBanner.Value && !_bannerHidden)
            {
                textComp.gameObject.SetActive(false);
                _bannerHidden = true;
                HHMods.Core.Plugin.Log?.LogInfo("[HordeBroadcastPoller] hid game's top-right horde banner");
            }
            else if (!Plugin.SuppressGameHordeBanner.Value && _bannerHidden)
            {
                textComp.gameObject.SetActive(true);
                _bannerHidden = false;
            }

            // Read text even if hidden — Text.text still updates when the GO is inactive.
            var current = textComp.text ?? "";
            if (current.Length > 0 && current != _lastText)
            {
                _lastText = current;
                ChatBox.Post(current, NoticeTag.Broadcast);
            }
        }
    }
}
