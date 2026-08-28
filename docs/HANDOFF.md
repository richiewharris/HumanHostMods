# Session Handoff — Human Host Mod Suite

**Last updated:** 2026-08-28
**Repo:** [github.com/richiewharris/-HumanHostMods](https://github.com/richiewharris/-HumanHostMods)
**Purpose:** Enough context for a fresh Claude session to pick up this work on another machine. Read this first, then `docs/DESIGN.md` for the full architecture.

---

## 1. Where we are

### Delivered
- **All scaffolding built.** Solution + 7 projects (`HHMods.Core`, `Minimap`, `Perks`, `QoL`, `Vehicles`, `Weapons`, `Recipes`). All build and load via BepInEx.
- **HHMods.Core** — shared plumbing: `MgrHub` accessors, `GameEvents.HubReady`, `Scheduler` (main-thread queue), skeleton registries (`PerkRegistry`, `RecipeRegistry`, `MinimapLayerRegistry`), `ModSidecar<T>` (versioned JSON per save slot), `Notify` helper.
- **HHMods.Minimap** — plugin entry, Harmony patches, `MinimapController` (compass bind + retry + enable/disable), `HotkeyBinding`, `FilterPanel` (IMGUI), `BuiltinLayers` (Player Waypoints + Merchants). All heavily-diagnosed after a chain of visual bugs.

### In progress (right at handoff)
Minimap is spawning, hotkey-toggleable (default **N**), and Harmony-bound to the game's `CompassPro` instance via a postfix on `World_Map_Mgr._Start`. **The map is enabled and visible in the top-right, but the render is BLACK with faint sky/cloud animation.** Three remaining issues:

1. **Camera appears to be looking UP at the sky, not down.** User reported faint clouds moving on the black map. Latest build sets:
   - `miniMapCameraMinAltitude = 100f`
   - `miniMapCameraMaxAltitude = 1000f`
   - `miniMapCameraHeightVSFollow = 250f`
   - `miniMapCaptureSize = 100f`
   Needs live-testing to confirm.
2. **Scroll wheel still zooms the minimap** (game uses scroll for hotbar). Just added Harmony `Prefix` patches returning `false` on `CompassPro.MiniMapZoomIn/Out/Toggle` — not yet verified.
3. **No cardinal indicators (N/S/E/W) or scale/radius key** — not yet implemented. Custom UI, not covered by CompassPro.

### What was fixed and shouldn't regress
| Symptom | Root cause | Fix location |
|---|---|---|
| "No CompassPro found" at load | Bind fired on Start scene before World scene loaded | Harmony postfix on `World_Map_Mgr._Start` + retry poll |
| Shift+M opened game's world map AND our minimap | Shift+M is game's own map key | Changed default hotkey to `N` |
| BepInEx kept stale `Show On Start = true` after code default flipped to `false` | Config file persists user-set values | Delete `BepInEx/config/io.hh.minimap.cfg` on default change |
| Silent build-deploy failure (game holds DLL) | `AfterBuild` target checked "does the file exist" instead of "did we copy it" | `Directory.Build.targets` now checks `Copy.CopiedFiles` output |
| Huge black rectangle covering main game view | CompassPro spawns 4 `Black_BG` curtain quads for world-map mode + `Image_BG` backdrop | `HideMinimapCurtains()` in `MinimapController` — disables both, restored on disable so world map still works |
| Whole game view went top-down after HDRP fix | Adding `HDAdditionalCameraData` reset `Camera.targetTexture` to null → cam rendered to screen instead of texture | `EnsureHDRPCameraData()` re-forces `targetTexture` after adding HDRP data; skips `CopyTo` (that was the trigger) |

---

## 2. Repo + build setup

**Workspace:** `C:\Users\SMC\HumanHostMods\` (this repo)

**Solution:** `HumanHostMods.sln` (7 projects). Build with:
```powershell
& "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe" `
  HumanHostMods.sln /p:Configuration=Debug /v:minimal /nologo
```

**Deploy target:** `C:\Program Files (x86)\Steam\steamapps\common\Human Host\BepInEx\plugins\HHMods\`
Auto-deploy on build via `Directory.Build.targets`. If deploy fails (game running holding the DLL), run `scripts\deploy.ps1` after closing the game, or PowerShell:
```powershell
Copy-Item -Path src\HHMods.Minimap\bin\Debug\HHMods.Minimap.dll -Destination "$env:USERPROFILE\..\..\Program Files (x86)\Steam\steamapps\common\Human Host\BepInEx\plugins\HHMods\" -Force
```

**Iteration loop:**
1. Edit `.cs` file
2. `MSBuild src\HHMods.Minimap\HHMods.Minimap.csproj /p:Configuration=Debug /v:minimal /nologo /p:DeployOnBuild=false`
3. Ask user to close game
4. Deploy via PowerShell `Copy-Item` (auto-deploy also works when game is closed)
5. Clear log: `Remove-Item "C:\Program Files (x86)\Steam\steamapps\common\Human Host\BepInEx\LogOutput.log" -Force`
6. User launches, loads save, presses N
7. Read log

---

## 3. Critical hook points (Cecil-verified)

Do NOT re-derive these — dumped from shipped DLLs with Mono.Cecil.

| System | Class::Member |
|---|---|
| Central hub | `Mgr_Hub` (MonoBehaviour, found via `FindObjectOfType`); exposes `_SkillMgr`, `_LootMgr`, `_TerraDigMgr`, `_CraftMgr`, `_CarMgr`, `_WorldMapMgr`, `_PlayerMgr`, `_Player_HotKeys`, `_SaveDataMgr`, `_NotificationSystem`, `_EquipmentMgr` |
| Manager singletons | `<Manager>.ins` (public property; private backing field `_ins`) |
| World map | `World_Map_Mgr.ins`, `._CompassPro: CompassPro`, `._Start()`, `.Enable_Disable_World_Map()`, `.Add_POI(Vector3, string)` |
| Compass | `CompassNavigatorPro.CompassPro` (200+ minimap-specific fields listed via Cecil), `CompassProPOI`, `MiniMapInteraction`. Minimap sub-feature: `showMiniMap`, `miniMapContents = WorldView`, `miniMapLocation`, `miniMapPositionAndSize = ControlledByCompassNavigatorPro`, `miniMapSize`, `miniMapFollow`, `miniMapCameraHeightVSFollow`, `miniMapCaptureSize`, `miniMapZoomIn/Out/Toggle` |
| Skills / perks | `Creature.All_Skills_Set._SurviveSkills/._CraftSkills/._FightSkills : TalentSkill[]`. `TalentSkill = { AllSkill _skill, Language_Text _class }`. `AllSkill = { Language_Text _name, _instruct, Sprite icon, int maxLv, bool isBuff, float buffPeriod, bool stackableBuff, SkillValue[] _values }`. `Skill_Data.Get_Learned_Skill_Lv(string skillNameEn)`. `Char_Skills` has dozens of `_xxx_Factor` runtime fields plus `_lootLevel`, `_craftLevel`. `Skill_Mgr.Char_Init_Talent(string, int)`, `Update_Skill_Property(AllSkill, int)`, `Get_TalentSkill(string)`, `Load_Skill_Data(Skill_Data)`, `Get_Miner_Dmg_Factor(string soundMatName, Char_Skills)` |
| Mining | `Terrain.Terrain_Dig.ins`, `.Dig_Terrain(RaycastHit, bool fromPlayer, bool collectDirtBlock)`, `.Drop_Dirt_Prop(...)`. Terrain material system via `Sound_Mat` (Sound_FX.dll) — `_ThisMatHP, _MatDensity, _IsMetal`, etc. `Skill_Mgr._MinerSoundMats: Sound_Mat[]` is the enumerable ore/rock list. |
| Digger runtime | `Digger.Modules.Runtime.Sources.DiggerMasterRuntime.Modify(Vector3, BrushType, ActionType, ...)` and async variants |
| Crafting | `UI.Craft_Mgr.ins`, `.Craft_Items._CraftItemsData: CraftItemData[]` where `CraftItemData = { Tag_Menu BigCategory, Vector2 LayoutCellSize / Spacing, bool DisplayName, PerIconData[] perIconData }`, `PerIconData = { AssetReference iconRef, int craftNum, Icon_Info iconInfo, float craftSeconds, PerMatData[] matsData }`, `PerMatData = { AssetReference matIcon, int matNeedCount }`. 11 workbench types: `HandMade, Campfire, CarpentryWorkbench, CuttingWorkbench, AnvilWorkbench, GunWorkbench, MechanicalWorkbench, BiochemicalWorkbench, ElectronicsWorkbench, Furnace, CementMixer` |
| Vehicles / COM | `Top_Info.Calculate_Mass_Center` — the sole hook point for the Gyroscope mod (verified by IL scan of `Rigidbody.set_centerOfMass` callers). `Car_BuildMode_Switch.Vehicle_Switch_To_NoKinematic` re-calls it on entering drive mode. |
| Vehicle Lua API | `Car.Car_Coding.LoadPlayerCode(string playerLuaCode)`, `Run_script()`, `Accelerate/Reverse/Steer_Left/Steer_Right/Brake/Rotate/Lock_Rot/Set_Rot_Angle`. xLua ships with the game. |
| Traps | `Trap.Trap_Base` (base MonoBehaviour) → `_TrapDamage, _TriggerCol, _TrapSoundSet`; `Trap_SensorSpike`, `Trap_Spike`, `Trap_Laser`, `Trap_RotBlade`. Registered via `Trap_Mgr.ins`. |
| Weapons | `Hand_Tools.Tool_Interacter` (base) → `_Damage, _HitDownProb, _BladeHitProb, _RepairValue, _RepairDis, Repair_Once(Build_Info)`. Subclasses: `Weapon_Melee`, `Weapon_Range` (guns AND bows — has `Get_Bow_Special_Info()`) |
| Damage pipeline | `Battle_Info.MinusHP(float minusValue)` — single damage entry. `Battle_Info.FatherBI: Build_Info` links to the parent. |
| Item tagging | `Item_Info.Icon_Info._Tag: string`, `._Tags: string[]`, `._SlotType: Slot_Type`. `Build_Info._ItemType: ItemType` for building items. |
| Loot | `UI.Loot_Mgr.ins`, `.Enable_Loot_Window(Transform, int, int, string belong_BI_Key, ContainerType, Loot_Rate_Sets, bool, bool)`, `.No_Items_Inside(string belong_BI_Key)`, `.Drop_Storage_Items_On_Ground(...)`. `Loot_Rate_Sets._LootSpawnRates: Loot_Spawn_Rate[]` = `{ string _spawnLootTag, float _spawnRateRange, float _stackFactor }` |
| Char status | `Creature.Char_Status._currentFood, _currentWater, _maxFood, _maxWater, _currHP, _maxHP, _currStamina, _maxStamina`. Private `Update_Food_UI()`, `Update_Water_UI()`. `Player_Mgr._FoodText, _FoodRect, _WaterText, _WaterRect` for HUD elements. |
| Hotkeys | `Player_HotKeys.ins` with per-action `Hotkey_Sets` (Bag, Map, Talent, Craft, FastPutItem, UseF, Reload, FlashLight, TakeAll, FirstPerson, Q_Menu, Sprint, Jump, Crouch, WASD, etc.) |
| Save | `SaveDataManager`, `Save_Player_Data`, EasySave3 (`EasySave.dll`). Save slots at `Human Host_Data/Save/Save_XX/`. |

---

## 4. Minimap-specific gotchas we discovered

- **CompassPro is SHARED** with the game's world map (`World_Map_Mgr._CompassPro`). Modifying it directly affects both. Our approach: modify only HUD-mode properties (non-`FullScreen*` fields), save originals, restore on disable, and re-run `DisableMiniMap()` (invoked via reflection since it's not public) to force teardown.
- **Game uses HDRP.** The `TopDownCamera` CompassPro creates is a bare `UnityEngine.Camera` and renders **black** under HDRP. Fix: add `UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData` component to it.
- **HDRP init resets `Camera.targetTexture` to null.** When you `AddComponent<HDAdditionalCameraData>`, the camera loses its RenderTexture binding and starts rendering **to the screen**. Fix: re-force `cam.targetTexture = miniMapTex` immediately after adding HDRP data.
- **Cloning `HDAdditionalCameraData` from Main_Camera via `CopyTo` was the trigger** of the "whole game top-down" bug — it copied `Frame Settings` / rendering hints that reoriented rendering. Skip the CopyTo — HDRP defaults on a fresh `HDAdditionalCameraData` are safe.
- **Four `Black_BG` UI Image quads spawn under `MiniMap Root`** to mask outside-the-map area in fullscreen mode. Three project off-screen, one covers the left ~75% of the screen in HUD mode. **Plus** an `Image_BG` (sprite `World_Map_Black`) that sits behind the minimap. Both must be disabled in HUD mode via `HideMinimapCurtains()`. Restore both in `TryDisableMinimap()` so the world map still works when opened.
- **Scroll wheel** is polled by CompassPro's own Update, not routed through the `MiniMapInteraction` component we disable. Only Harmony `Prefix(return false)` on `MiniMapZoomIn/Out/Toggle` reliably stops it.
- **`Camera.main` on this game returns `GPUI_Culling_Cam`**, not the player camera. Don't use it as a follow target. Use `Object.FindObjectsOfType<Player_Input>(false)[0].transform` — `Player_Input` marks the player character (NPCs have `NPC_Input`).
- **The `_ins` naming** on singletons is a **property** (`ins`), not the underscore-prefixed field. Cecil dumped `static private <T> _ins` (field) and `get_ins()` / `set_ins()` methods. Access with `<Type>.ins`.

---

## 5. Immediate next actions (in order)

1. **Verify the altitude fix works.** Ask user to close game, redeploy (auto-deploy or `Copy-Item`), launch, load save, press N. Check whether the minimap now shows terrain (not sky/black).
2. **Verify the scroll-zoom Harmony patch worked.** Same test — press N, then scroll mouse wheel. Should switch hotbar slots WITHOUT resizing minimap.
3. **Add cardinal indicators (N/S/E/W).** Custom UI: 4 Text elements anchored around the minimap edge, updated each frame based on the follow-transform's Y rotation. Small feature.
4. **Add scale/radius key.** Small Text showing `miniMapCaptureSize` in meters below the minimap.
5. **Wave 1 wrap:** finish 3 recipes (Water, Gunpowder, Steel Chest) in `HHMods.Recipes` — validates the recipe registry pipeline for the first time.
6. **Move to Wave 2 (Perks)** — starting with `Panning Out` (simplest, hooks `Terrain_Dig.Dig_Terrain` postfix). Then `Geologist` (validates cross-plugin layer registration into the minimap).

---

## 6. Git setup notes

Repo pushed as **`richiewharris`** account. Local ssh needed a per-host alias because both accounts had keys and the wrong one was winning. Config in `~/.ssh/config`:

```
Host github.com
  User git
  Hostname github.com
  IdentityFile ~/.ssh/id_rsa
  IdentitiesOnly yes

Host github-rwh
  User git
  Hostname github.com
  IdentityFile ~/.ssh/rwh.key
  IdentitiesOnly yes
```

This repo's remote uses the alias: `git@github-rwh:richiewharris/-HumanHostMods.git`

On another machine, mirror the same `~/.ssh/config` entries (with matching key files present) or clone via HTTPS.

---

## 7. Design source of truth

Read [DESIGN.md](DESIGN.md) for the full architecture: 22 mods across 5 categories, 3 confidence tiers, 7 delivery waves, dependency graph.

---

## 8. What the user cares about

- **Ships as BepInEx plugins**, not Steam Workshop (their decision).
- **User is highly technical** — no need to over-explain, they'll push back if a diagnosis doesn't hold water. Show diagnostic reasoning; don't hand-wave.
- **User values good iteration ergonomics** — auto-deploy that works, clean logs, honest error surfacing.
- **User has good architectural instincts** — e.g. suggested the "shadow of the border" theory that led us to the curtain-quad discovery.
