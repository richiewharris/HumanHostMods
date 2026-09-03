using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace HHMods.Weapons
{
    /// <summary>
    /// Weapons module. Currently ships a first-pass throwing prototype (<see cref="ThrowController"/>)
    /// that captures the same "hold to charge, release to launch" mechanic as the game's bow but
    /// as a standalone controller (no bow animation state machine, no arrow-model reuse — a bare
    /// physics sphere flies from the camera on release). Validates the projectile pipeline
    /// before we build the explosive mine and grenade on top of it.
    /// </summary>
    [BepInPlugin(PluginId, "HH Weapons", "0.1.0")]
    [BepInDependency(HHMods.Core.Plugin.PluginId)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginId = "io.hh.weapons";

        internal static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }

        // ---- Throw prototype config ----
        internal static ConfigEntry<UnityEngine.KeyCode> ThrowKey;
        internal static ConfigEntry<float> MaxThrowForce;
        internal static ConfigEntry<float> MinThrowForce;
        internal static ConfigEntry<float> MaxChargeSeconds;
        internal static ConfigEntry<float> ProjectileMass;
        internal static ConfigEntry<float> ProjectileScale;
        internal static ConfigEntry<float> ProjectileLifetimeSeconds;
        internal static ConfigEntry<bool>  DrawTrail;
        internal static ConfigEntry<UnityEngine.KeyCode> ClipDumpKey;
        internal static ConfigEntry<UnityEngine.KeyCode> BoneDumpKey;

        // ---- Throw animation ----
        internal static ConfigEntry<bool>   EnableThrowAnimation;
        internal static ConfigEntry<string> ThrowAnimationClipName;
        internal static ConfigEntry<float>  AnimationSpawnFraction;
        internal static ConfigEntry<float>  AnimationSpeed;
        internal static ConfigEntry<bool>   UseUpperBodyLayer;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            ThrowKey = Config.Bind("Throw", "Throw Key", UnityEngine.KeyCode.G,
                "Hold to charge, release to throw. Bare KeyCode (no modifier) to keep it easy to bind.");
            MaxThrowForce = Config.Bind("Throw", "Max Throw Velocity (m/s)", 25f,
                new ConfigDescription("Muzzle velocity at full charge.", new AcceptableValueRange<float>(1f, 100f)));
            MinThrowForce = Config.Bind("Throw", "Min Throw Velocity (m/s)", 6f,
                new ConfigDescription("Muzzle velocity at zero charge (still gives a lobbed toss).", new AcceptableValueRange<float>(0f, 50f)));
            MaxChargeSeconds = Config.Bind("Throw", "Max Charge Seconds", 1.5f,
                new ConfigDescription("How long you have to hold the throw key to reach full charge.", new AcceptableValueRange<float>(0.1f, 5f)));
            ProjectileMass = Config.Bind("Throw", "Projectile Mass (kg)", 0.5f,
                new ConfigDescription("Rigidbody mass. Higher = more inertia + more gravity drop.", new AcceptableValueRange<float>(0.01f, 20f)));
            ProjectileScale = Config.Bind("Throw", "Projectile Diameter (m)", 0.15f,
                new ConfigDescription("Visible sphere diameter.", new AcceptableValueRange<float>(0.05f, 2f)));
            ProjectileLifetimeSeconds = Config.Bind("Throw", "Projectile Lifetime (s)", 15f,
                new ConfigDescription("Projectile auto-destroys after this many seconds so the world doesn't fill with test balls.", new AcceptableValueRange<float>(1f, 120f)));
            DrawTrail = Config.Bind("Throw", "Draw Trail", true,
                "Adds a bright trail behind the projectile so the trajectory is visible even if the HDRP material fails to render.");
            ClipDumpKey = Config.Bind("Diagnostic", "Animation Clip Dump Key", UnityEngine.KeyCode.F10,
                "Press to dump all currently loaded AnimationClips to log + plugins/HHMods/animation-clips.txt. Equip weapons first so their clips get streamed in.");
            BoneDumpKey = Config.Bind("Diagnostic", "Player Bone Dump Key", UnityEngine.KeyCode.F11,
                "Press to dump the local player's full bone hierarchy + Animator info to log + plugins/HHMods/player-bones.txt. Used for authoring custom throw animations.");

            EnableThrowAnimation = Config.Bind("Throw Animation", "Enable (experimental)", false,
                "OFF by default — the current procedural animation implementation causes the character mesh to disappear during the throw. Set to true only to iterate on the animation code. A proper animation using an AssetBundle-imported clip from a real Mixamo FBX is the intended fix.");
            ThrowAnimationClipName = Config.Bind("Throw Animation", "Clip Name", "RM_Right_Attack_Charged_01",
                "Name of the AnimationClip to play on the player when a throw is released. Must be a clip currently loaded (see F10 diagnostic dump). Good candidates: RM_Right_Attack_Charged_01, Axe_Combo_5, Hand_Combo_3.");
            AnimationSpawnFraction = Config.Bind("Throw Animation", "Projectile Spawn Fraction", 0.55f,
                new ConfigDescription("At what fraction of the clip length the projectile actually spawns. 0 = spawn immediately on release; 0.5 = spawn halfway through the swing; 1.0 = spawn at follow-through end. 0.55 targets the peak-forward moment of a typical cocked swing.",
                    new AcceptableValueRange<float>(0f, 1f)));
            AnimationSpeed = Config.Bind("Throw Animation", "Animation Playback Speed", 1.4f,
                new ConfigDescription("Speed multiplier on the animation. >1 = faster swing (snappier throw); <1 = slower.",
                    new AcceptableValueRange<float>(0.25f, 4f)));
            UseUpperBodyLayer = Config.Bind("Throw Animation", "Use Upper Body Layer", true,
                "If true, plays on the upper-body Animancer layer so legs keep doing their walk/idle. If false, plays on the base layer (may look better standing still, worse while moving).");

            gameObject.AddComponent<ThrowController>();

            Log.LogInfo($"{PluginId} loaded — throw prototype online (default key: {ThrowKey.Value})");
        }
    }
}
