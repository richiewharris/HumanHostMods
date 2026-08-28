using System;
using System.IO;
using UnityEngine;

namespace HHMods.Core
{
    /// <summary>
    /// Per-save-slot sidecar file for mod state. Stored as JSON alongside the game's
    /// save data at <c>Human Host_Data/Save/&lt;slot&gt;/mod_&lt;id&gt;.json</c>.
    /// Uninstalling the mod leaves the sidecar untouched (no orphan cleanup).
    /// </summary>
    public sealed class ModSidecar<T> where T : class, new()
    {
        public string ModId { get; }
        public int SchemaVersion { get; }
        private readonly Func<T> _defaultFactory;

        public ModSidecar(string modId, int schemaVersion, Func<T> defaultFactory = null)
        {
            if (string.IsNullOrEmpty(modId)) throw new ArgumentException("modId is required.");
            ModId = modId;
            SchemaVersion = schemaVersion;
            _defaultFactory = defaultFactory ?? (() => new T());
        }

        public T Load(string saveSlotFolder)
        {
            if (string.IsNullOrEmpty(saveSlotFolder) || !Directory.Exists(saveSlotFolder))
            {
                Plugin.Log.LogDebug($"[Sidecar/{ModId}] slot folder missing → defaults");
                return _defaultFactory();
            }
            var path = PathFor(saveSlotFolder);
            if (!File.Exists(path)) return _defaultFactory();
            try
            {
                var json = File.ReadAllText(path);
                var envelope = JsonUtility.FromJson<Envelope>(json);
                if (envelope == null)
                {
                    Plugin.Log.LogWarning($"[Sidecar/{ModId}] envelope null at {path} — defaults");
                    return _defaultFactory();
                }
                if (envelope.schema != SchemaVersion)
                {
                    Plugin.Log.LogWarning($"[Sidecar/{ModId}] schema {envelope.schema} != expected {SchemaVersion}, using defaults (file kept for manual migration)");
                    return _defaultFactory();
                }
                var payload = JsonUtility.FromJson<T>(envelope.payload ?? "{}");
                return payload ?? _defaultFactory();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Sidecar/{ModId}] load failed at {path}: {e}");
                return _defaultFactory();
            }
        }

        public void Save(string saveSlotFolder, T data)
        {
            if (string.IsNullOrEmpty(saveSlotFolder))
            {
                Plugin.Log.LogWarning($"[Sidecar/{ModId}] Save() called with empty slot folder — skipping");
                return;
            }
            try
            {
                Directory.CreateDirectory(saveSlotFolder);
                var path = PathFor(saveSlotFolder);
                var envelope = new Envelope { schema = SchemaVersion, payload = JsonUtility.ToJson(data ?? _defaultFactory()) };
                var json = JsonUtility.ToJson(envelope, prettyPrint: true);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Sidecar/{ModId}] save failed: {e}");
            }
        }

        private string PathFor(string saveSlotFolder) => Path.Combine(saveSlotFolder, $"mod_{ModId}.json");

        [Serializable]
        private class Envelope
        {
            public int schema;
            public string payload;
        }
    }
}
