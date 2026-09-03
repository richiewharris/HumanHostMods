using System;
using System.Collections.Generic;
using UnityEngine;

namespace HHMods.Core
{
    /// <summary>
    /// Persistent MonoBehaviour that drives:
    ///   1. A main-thread action queue (<see cref="Post"/>) for anything called from non-Unity threads.
    ///   2. Polling for <see cref="GameEvents"/>.HubReady — fires once Mgr_Hub._ins is non-null.
    /// </summary>
    public class Scheduler : MonoBehaviour
    {
        private static readonly Queue<Action> _queue = new Queue<Action>();
        private static readonly object _gate = new object();

        public static void Post(Action a)
        {
            if (a == null) return;
            lock (_gate) _queue.Enqueue(a);
        }

        private void Update()
        {
            // Drain queue
            while (true)
            {
                Action a;
                lock (_gate)
                {
                    if (_queue.Count == 0) break;
                    a = _queue.Dequeue();
                }
                try { a(); }
                catch (Exception e) { Plugin.Log.LogError($"[Scheduler] queued action threw: {e}"); }
            }

            GameEvents.Poll();
        }
    }
}
