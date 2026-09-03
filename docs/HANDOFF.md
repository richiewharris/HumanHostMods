# Session Handoff — Human Host Mod Suite

**Last updated:** 2026-09-02
**Repo:** [github.com/richiewharris/-HumanHostMods](https://github.com/richiewharris/-HumanHostMods)
**Purpose:** Complete context for a fresh Claude session on another machine. Read this first, then `docs/DESIGN.md` for the architecture the suite was scaffolded against, and `docs/PERKS_SPEC.md` for the specific perks-rebuild plan.

---

## 0. User + workflow rules (must-know)

- **NEVER use em dashes (`—`) in any output.** Use commas, colons, periods, or parentheses instead. This is a hard user preference.
- User email for authorship: `richie.w.harris@gmail.com`. Do not send to external services unless the user explicitly asks.
- User's dev machine changes but the game install layout is stable.

---

## 1. Environment

### Game

- **Human Host** (Steam). Unity 2022.3.62, HDRP, Mono. Live app version at last session: `EA v0.8.307`.
- Default install: `C:\Program Files (x86)\Steam\steamapps\common\Human Host\`
- Managed assemblies: `Human Host_Data\Managed\`
- **BepInEx 5.4.23.2** is pre-installed. Plugins land at `BepInEx\plugins\HHMods\`. Log at `BepInEx\LogOutput.log`. Configs at `BepInEx\config\io.hh.*.cfg`.

### Local dev machine setup

The new machine needs:

1. **Game installed** at the same path (or override via `HHGameDir` env var or MSBuild `-p:GameDir=...`).
2. **.NET Framework 4.7.2** targeting pack (needed because BepInEx 5 runs on Mono/.NET 4.x).
3. **MSBuild** (via Visual Studio 2019 Build Tools or full VS). Path used at last session:
   `C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe`
4. **PowerShell** for build + deploy commands.
5. **Unity 2022.3.62 LTS** for the animation pipeline (Section 6). User confirmed installed at last session.
6. **git**. Repo remote is user's GitHub. Working directory: `C:\Users\SMC\HumanHostMods\` (was on last machine).

### Build + deploy

Every project auto-deploys to `BepInEx\plugins\HHMods\` after a successful build. Auto-deploy fails silently (with a warning) if the game is running (DLL locked). The workflow is:

```powershell
& "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe" `
  "src\HHMods.Weapons\HHMods.Weapons.csproj" /p:Configuration=Release /v:minimal /nologo
```

Build all projects with `HumanHostMods.sln`. Deploy is configured in `Directory.Build.targets`.

### Auto-deploy failure signature

When the game is running, output includes `AUTO-DEPLOY FAILED for <dll> — game is likely running (DLL locked)`. Ask user to close the game and rebuild.

---

## 2. Solution layout (7 projects)

| Project | Status | Purpose |
|---|---|---|
| `HHMods.Core` | Shipped | Shared registries, `MgrHub` accessors, `GameEvents.HubReady`, `Scheduler`, `ModSidecar<T>`, `Notify` |
| `HHMods.Minimap` | Shipped + polished | V2 from-scratch minimap. See §3.1. |
| `HHMods.QoL` | Shipped + polished | ChatBox notification log. See §3.2. |
| `HHMods.Perks` | Deprecated in place | Sliders that clobber `Char_Skills`. Retired pending rebuild. See §3.3 + `PERKS_SPEC.md`. |
| `HHMods.Recipes` | Shipped, buggy | Per-workbench craft speed + global material cost + yield. Reflection init fails. See §3.4. |
| `HHMods.Vehicles` | Stub only | Gyroscope stabilizer. Not started. |
| `HHMods.Weapons` | Shipped, animation blocked | Rock-throwing prototype (G to charge). See §3.5. |

---

## 3. Module state (detailed)

### 3.1 Minimap V2 — FULLY SHIPPED

Custom-built from scratch, replaced original CompassPro-clone V1. Owns its own Camera, RenderTexture, Canvas.

**Features:** top-down world render, bezel with cream hairlines + scrollwork ring + cardinal/ordinal diamonds, compass badge with 4-point star, kite player arrow, zoom badges (+ and - stacked right side), coord bar centered above (`( X, Z )  H Y`), biome nameplate below (styled to match bezel), POI overlay (uses `CompassProPOI` scan with live sprite + visited-state refresh, edge-off-map hidden).

**Key files:**
- `src/HHMods.Minimap/V2/MinimapV2Controller.cs` (~900 lines)
- `src/HHMods.Minimap/V2/MinimapV2HotkeyBinding.cs`
- `src/HHMods.Minimap/UI/MinimapHUDBar.cs` (coord + biome bars, uses `MinimapV2Controller.TryGetChromeScreenRect`)
- `src/HHMods.Minimap/Plugin.cs`

**Config:** `io.hh.minimap.cfg`. `Toggle Minimap` = N by default. `Tuning Panel` = F9 (unused in V2). Size, zoom, exposure, saturation, arrow size, culling toggles.

**Deferred polish (not blocking):**
- Wire V2 into shared tuning panel (V1 has one, V2 uses config only)
- Glass sphere overlay effect (aesthetic — pale highlight + edge meniscus)

### 3.2 ChatBox — FULLY SHIPPED

Persistent notification log with tag-based color coding, filter chips, font size picker (12/14/16/18), text wrap, flat matte styling.

**Recently moved:** anchor is now bottom-left (above health bar) instead of bottom-right. One-shot migration in `Plugin.Awake` picks up old configs. Hidden when `Cursor.lockState != Locked` so it doesn't show over menus / inventory / ESC panel.

**Key files:**
- `src/HHMods.QoL/ChatBox/ChatBox.cs` (renders panel + chips + picker via IMGUI)
- `src/HHMods.QoL/ChatBox/NotificationInterceptor.cs` (Harmony patches on `Add_Notice` overloads)
- `src/HHMods.QoL/ChatBox/HordeBroadcastPoller.cs` (polls `NPC_Horde_Mgr._NextHordeText` via reflection every 1s)
- `src/HHMods.QoL/Plugin.cs`

**Config:** `io.hh.qol.cfg`. Anchor, margins, font size, filter toggles, notification bubble suppression.

**Known quirk:** IMGUI can't z-order behind a Unity UI Canvas, hence the cursor-lock workaround. If we later want ChatBox to overlay menus intentionally, that's a design decision, not a bug.

### 3.3 Perks — DEPRECATED, REBUILD PLANNED

**Current impl** (in `src/HHMods.Perks/Plugin.cs`): 14 BepInEx sliders that write directly to `Char_Skills` modifier fields (miner damage, wood damage/yield, loot boost, durability, vitals, weapon damage) on the local player. **Clobbers the game's own earned perks.** Every one of my 14 sliders duplicated an existing game perk from `Skill_Mgr._SkillSets`.

**Rebuild spec:** `docs/PERKS_SPEC.md` (4 milestones, 5 new perks that fill gaps in the game's tree). Not started implementing.

The five perks planned:
1. **Pitcher** (Combat, 5r) — +5 yards throwing range per rank
2. **Panning Out** (Survival, 10r) — 10% per rank chance of bonus mining drop (50% quantity)
3. **Geologist** (Survival, 10r) — +10 yd per rank ore-node reveal on minimap + world map
4. **Alchemist** (Craft, 10r) — 5% per rank ingredient recovery at Biochemical + Campfire
5. **Metallurgist** (Craft, 10r) — same but Furnace

**Diagnostic already shipped:** `PerkDumper` in the current Perks module dumps every game TalentSkill (name / description / icon / per-rank values) to `plugins/HHMods/perk-dump.txt` on first successful apply. Dump was captured last session (see file if present).

### 3.4 Recipes — BROKEN

Shipped module attempts per-workbench craft speed + global material cost + yield via Harmony patch on `Craft_Items.OnEnable` that mutates `_CraftItemsData`. **Reflection init fails** with `[Recipes] reflection init failed — nested types not found. Was Craft_Items shape changed?` in the log. Module is inert.

Fix needed: `CraftItemData`, `PerIconData`, and `PerMatData` are nested `NestedAssembly` types inside `Craft_Items`. The reflection lookup in `RecipeModifier.cs` needs adjustment. Fix is a prerequisite for Perks M3 (Alchemist/Metallurgist share the material-consumption hook).

### 3.5 Weapons — ROCK THROW PROTOTYPE, ANIMATION BLOCKED

**Working:**
- G key hold-to-charge, release to throw
- Physics sphere spawns at right-hand transform, forward from camera
- Configurable velocity, mass, scale, charge time, lifetime
- Grey rocky tint via HDRP/Unlit material lookup (with Sprites/Default fallback)
- Pale dust `TrailRenderer` for trajectory visibility
- `ProjectileDiagnostic` component logs each collision (target name, layer, velocity at hit), duplicate hits per creature suppressed
- F10 hotkey: `AnimationClipDumper` writes every currently-loaded clip to `plugins/HHMods/animation-clips.txt`
- F11 hotkey: `PlayerBoneDumper` writes full bone hierarchy + humanoid bone map to `plugins/HHMods/player-bones.txt`

**Not working (as of last session):**
- Throw animation. Two attempts, both failed:
  1. Animancer clip playback (`RM_Right_Attack_Charged_01`): animation played but pose was axe-swing style, not throw. Not a rock throw motion.
  2. Procedural via `HumanPoseHandler` muscles: **character mesh disappears for the duration of the animation**, projectile doesn't fire. Fixes attempted (`updateWhenOffscreen = true` on all SkinnedMeshRenderers, save/restore `bodyPosition` + `bodyRotation`) did not resolve.

**Currently disabled:** `Enable (experimental) = false` in `io.hh.weapons.cfg`. Throws land without animation.

**Key files:**
- `src/HHMods.Weapons/Plugin.cs` — config
- `src/HHMods.Weapons/ThrowController.cs` — key handling, launch flow, projectile spawn, hand transform resolution, ChatBox notify (soft dep via reflection)
- `src/HHMods.Weapons/ProceduralThrowRunner.cs` — HumanPose muscle driver (currently non-functional)
- `src/HHMods.Weapons/ThrowVariant.cs` — 4 seed variants in muscle space (Overhand Baseball, Sidearm Whip, Underhand Toss, Quick Snap)
- `src/HHMods.Weapons/AnimationClipDumper.cs` — F10 diagnostic
- `src/HHMods.Weapons/PlayerBoneDumper.cs` — F11 diagnostic

---

## 4. Game reference data captured

Diagnostic dumps written to `BepInEx/plugins/HHMods/`:

- `perk-dump.txt` — full game perk tree (Combat 12, Survival 15, Craft 8), each entry with name / description / icon name / per-rank values. Captured 2026-09-01.
- `animation-clips.txt` — every AnimationClip loaded in the scene at F10 press. Captured 2026-09-02 with axe equipped and swung. 211 clips. Buckets: ATTACK/MELEE (35), BOW/ARROW (9), HAND/THROW/GRIP (25), OTHER (141).
- `player-bones.txt` — Hunter (local player) full transform hierarchy. **Rig is Mixamo humanoid**, `mixamorig_*` bone names throughout. Avatar is `Hunter_Mixamo_BlinkAvatar`, `isHuman: True`. `hasRoot: False` (no root motion). Full humanoid bone map from Unity's `HumanBodyBones` to Mixamo names included.

**The rig being Mixamo humanoid is critical** — it means any Mixamo animation retargets onto Hunter automatically. This unlocks §6.

---

## 5. Key game manager references (learned via Cecil)

Managers surfaced through `HHMods.Core.MgrHub`:
- `Skill_Mgr` (`Creature.dll`) — talent trees at `_SkillSets._FightSkills / _SurviveSkills / _CraftSkills`, XP + level via `GetLevelByTotalExp` etc.
- `Char_Skills` (`Creature.dll`) — ~50 float modifier fields the game's perks write to
- `Craft_Mgr` (`UI.dll`) — `.ins._PlayerCraftItem._CraftItemsData` array holds recipes
- `Weapon_Range` (`Hand_Tools.dll`) — bow / gun. Bow state via `_isBow`, `_bowState`, `_corPullArrow`, `_corRelease`, `_pullRate`
- `Weapon_Melee` (`Hand_Tools.dll`) — attack anim sets, `_lastAtckClip`, chop / swing / combo
- `Arrow_Impact` (`Hand_Tools.dll`) — projectile behavior (physics + damage)
- `C_Controller_Base` (`Creature.dll`) — animator + Animancer layers on the player character. `_3rd_Animancer`, `_basicBodyLayer`, `_UpperBodyLayer`, `animator`, `avatar`
- `Weather_Controller` (`Weather.dll`) — `.ins._currentWeatherZone.gameObject.name` gives biome, used by minimap biome nameplate
- `CompassProPOI` (`CompassPro.dll`) — POI markers used by both world map and our minimap overlay
- `Player_Input` (`Creature.dll`, not Player.dll despite the name) — main player controller
- `World_Map_Mgr` (`Player.dll`) — world map. `POIUnderMouse` field is `CompassProPOI` type

Key referenced DLLs are in `Directory.Build.props`. Added recently: `Weather.dll`, `Kybernetik.Animancer.dll`, `Language.dll`.

---

## 6. NEXT SESSION: Unity animation pipeline

**This is the priority path.** User has:
- Unity 2022.3.62 LTS installed
- A downloaded Mixamo throw FBX at `docs/animations/Throw Object.fbx` (696 KB binary FBX)

**Goal:** Load the throw animation from the FBX at mod runtime and play it via Animancer on Hunter's `_3rd_Animancer._UpperBodyLayer`. The Animancer play code path worked in an earlier iteration (character stayed visible, played `RM_Right_Attack_Charged_01`) — it's only the direct HumanPose muscle write that broke rendering.

**Step-by-step Unity workflow (~2-3 hours):**

1. **Create a Unity 2022.3.62 project.** Standard 3D template is fine (URP/HDRP doesn't matter for this — we're only building an AssetBundle of the clip).

2. **Import a Mixamo humanoid rig for retargeting.** Download the free "X Bot" from mixamo.com (or any humanoid). Set its Rig to Humanoid in the FBX importer.

3. **Import `Throw Object.fbx`.**
   - FBX Import Settings → Rig tab → Animation Type: **Humanoid**, Avatar Definition: **Copy From Other Avatar**, select X Bot's avatar. This makes the clip retargetable onto any humanoid.
   - FBX Import Settings → Animation tab → confirm clip is imported. Rename to `Throw_Rock` or similar. Set Loop = off. Trim start/end if the FBX has T-pose padding.
   - Optionally place X Bot in a scene and use the Animation window to preview the clip playing on it.

4. **Extract the AnimationClip to a standalone asset.** Right-click clip in Project → Duplicate. Move the duplicated `.anim` to `Assets/ThrowClips/Throw_Rock.anim`.

5. **Build an AssetBundle.** Create an Editor script at `Assets/Editor/BuildBundle.cs`:

   ```csharp
   using UnityEditor;
   using UnityEngine;
   public class BuildBundle
   {
       [MenuItem("HHMods/Build Throw Bundle")]
       public static void Build()
       {
           AssetImporter.GetAtPath("Assets/ThrowClips/Throw_Rock.anim").assetBundleName = "throws";
           var outDir = "AssetBundles/StandaloneWindows64";
           System.IO.Directory.CreateDirectory(outDir);
           BuildPipeline.BuildAssetBundles(outDir,
               BuildAssetBundleOptions.None,
               BuildTarget.StandaloneWindows64);
           Debug.Log($"Bundle built to {outDir}");
       }
   }
   ```
   Menu → HHMods → Build Throw Bundle. Bundle appears at `AssetBundles/StandaloneWindows64/throws`.

6. **Ship the bundle.** Copy `throws` into `BepInEx/plugins/HHMods/animations/throws`. Add to the mod's asset-loading path.

7. **Load + play in HHMods.Weapons.** In `ThrowController` (or a new `ThrowAnimationLoader`):

   ```csharp
   var bundlePath = Path.Combine(pluginDir, "animations", "throws");
   var bundle = AssetBundle.LoadFromFile(bundlePath);
   var throwClip = bundle.LoadAsset<AnimationClip>("Throw_Rock");
   // Then in ThrowSequence:
   var state = charBase._UpperBodyLayer.Play(throwClip);
   state.Speed = Plugin.AnimationSpeed.Value;
   yield return new WaitForSeconds(throwClip.length * Plugin.AnimationSpawnFraction.Value);
   Launch(charge01, charBase);
   // ... wait remainder, fade layer weight down
   ```

   The existing `PlayClipOnPlayer` method in `ThrowController.cs` already does this — just needs the clip source swapped from `Resources.FindObjectsOfTypeAll<AnimationClip>()` (name lookup) to the loaded bundle asset.

**Why this will work when muscle writes didn't:** Animancer.Play() runs through Unity's Animator pipeline, which handles humanoid retargeting cleanly. The character stays visible because the pose comes through the same code path the game's own animations use. The muscle-write approach bypassed that pipeline and hit some edge case that killed the SkinnedMeshRenderer.

**Estimated time:** 1-2 hours for the Unity + bundle work, 30 min for the mod-side load + wire-up.

---

## 7. Open todos, ranked

1. **Ship the Unity throw-animation pipeline** (§6). Highest priority — throwing is currently animation-less.
2. **Fix `HHMods.Recipes` reflection init.** Module is inert. Blocks Perks M3.
3. **`HHMods.Perks` rebuild per `PERKS_SPEC.md`.**
   - M1: retire current sliders, build `PerkInjector` service, ship **Pitcher** perk
   - M2: **Panning Out**
   - M3: **Alchemist + Metallurgist** (needs Recipes fix)
   - M4: **Geologist** (integrates with minimap POI overlay)
4. **`HHMods.Vehicles`.** Original plan: gyroscope stabilizer (self-righting torque when a vehicle flips). Not started; `Plugin.cs` is stub.
5. **Minimap V2 polish deferred items.** Shared tuning panel wiring + glass overlay effect.

---

## 8. Ongoing conventions / gotchas

- **Nested private types accessed via reflection.** `Craft_Items.PerIconData`, `Skill_Mgr.AllSkill`, `All_Skills_Set.TalentSkill` are all `NestedAssembly` or `NestedPrivate`. Use `System.Type.GetType("Class, Assembly").GetNestedType("Name", BindingFlags.NonPublic | BindingFlags.Public)` and cached `FieldInfo`.
- **Enum values that look nested and public may still be inaccessible.** `Craft_Mgr.WorkbenchType` is `NestedPublic` and accessible; check with Cecil before assuming.
- **Auto-deploy locks.** Game must be closed for `.dll` copy to succeed. Build output includes `AUTO-DEPLOY FAILED` on lock; ask user to close and rebuild.
- **`Cecil.dll`** for game DLL introspection lives at `C:\Program Files (x86)\Steam\steamapps\common\Human Host\BepInEx\core\Mono.Cecil.dll`. PowerShell + `[Mono.Cecil.AssemblyDefinition]::ReadAssembly(...)` is the pattern used all session.
- **Wiki extract** at `wiki/assets/raw/{level0,level1,globalgm,zb_*}/ExportedProject/Assets/Scripts/` has AssetRipper output, but most scripts are dummy stubs. Useful for enumerating classes and getting scene folder names (e.g., biome names like `zb_desert`, `zb_forest`, `zb_winterforest`) but not for reading actual game logic. Use Cecil against the real DLLs for that.
- **Config `SettingChanged`.** Prefer subscribing via `Config.SettingChanged` (ConfigFile-level) rather than per-entry, because `ConfigEntryBase` doesn't expose `SettingChanged`, only the generic `ConfigEntry<T>` does.

---

## 9. Files to skim on a fresh session

Read in this order to get up to speed fast:

1. This file (`docs/HANDOFF.md`)
2. `docs/PERKS_SPEC.md` — perks rebuild plan
3. `docs/DESIGN.md` — original architecture (some sections stale, but the registry pattern + load order still hold)
4. `src/HHMods.Core/Hub/MgrHub.cs` — accessor surface
5. `src/HHMods.Core/Util/GameEvents.cs` — lifecycle hooks
6. `src/HHMods.Weapons/ThrowController.cs` — current throw impl
7. `src/HHMods.Minimap/V2/MinimapV2Controller.cs` — polished V2 minimap (reference for how a complete module looks)
8. The three dump files in `BepInEx/plugins/HHMods/`

---

## 10. Recent decisions worth preserving

- **Deprecating `HHMods.Perks` in favor of injecting real `TalentSkill` entries into the game's panel.** The game's Combat/Survival/Craft tabs already exist; adding perks there is more polished than a parallel UI and avoids clobbering earned perks.
- **Muscle-space (HumanPose) authoring rejected for throw animation.** Character mesh disappears. Falling back to Unity-Editor-authored clips via AssetBundle.
- **Rock projectile is a bare Unity primitive** with an HDRP/Unlit grey material + dust `TrailRenderer`. Sufficient for prototype; real rock model comes later.
- **ChatBox anchor moved to bottom-left** (above health bar) after menu-overlap issue. Cursor.lockState is the menu-open detector.
- **POI overlay on minimap** uses `Object.FindObjectsOfType<CompassProPOI>(includeInactive: true)` and computes visibility ourselves (ignoring `miniMapIsVisible`, which only gets set by CompassPro's own tick that we've replaced). Filters out any POI whose transform is a descendant of the follow target (player).
