# Human Host Mod Suite — Design Document

**Status:** Draft v1
**Author:** SMC + Claude
**Last updated:** 2026-08-27
**Target game:** Human Host (Unity 2022.3.62, Mono)
**Loader:** BepInEx 5.4.23.2 (already installed)

---

## 1. Overview

This suite delivers 22 mods across five categories — perks, HUD/QoL features, crafted items, vehicle mods, and basic recipes — as a set of cooperating BepInEx plugins sharing a common core library.

The game ships with unusually favorable modding infrastructure: pure Mono assemblies with PDBs, an integrated `CompassPro` minimap engine, a first-class `Skill_Mgr` talent system, a data-driven crafting pipeline, a central `Mgr_Hub` singleton exposing every manager, and Singularity Hot Reload. Most items in this suite land as small, well-targeted Harmony patches plus a handful of new MonoBehaviours.

## 2. Goals

- Ship each mod as an independently installable BepInEx plugin. Any subset can be removed without breaking the others.
- Share plumbing (perk registration, recipe registration, minimap layers, save sidecar) through one core library.
- Perk-gated capabilities plug into shared systems cleanly — the minimap exposes a public layer-registration API that the perks mod fills in for "Geologist" without either side hardcoding the other.
- Iteration-friendly: rebuild triggers hot reload; each plugin can be developed and tested in isolation.
- Zero UI framework reinvention — reuse `CompassPro`, `DuloGames.UI`, and the game's existing tooltip/notification systems.

## 3. Non-Goals

- No Steam Workshop distribution in v1 (per user decision). All shipping via `BepInEx/plugins/`.
- No IL2CPP support (game is Mono).
- No new asset pipeline — all art assets bundled as embedded resources or AssetBundles built with the shipped Unity version.
- No multiplayer considerations in v1 (game appears single-player based on save-slot structure).

## 4. Architecture

### 4.1 Solution layout (7 projects)

| Project | Kind | Purpose |
|---|---|---|
| `HHMods.Core` | Shared library + BepInPlugin | Registries, `Mgr_Hub` accessors, save sidecar, notification helpers |
| `HHMods.Minimap` | BepInPlugin | Minimap HUD panel, layer registry service, built-in layers, filter UI |
| `HHMods.Perks` | BepInPlugin | Alchemist, Metallurgist, Geologist, Panning Out, Pitcher |
| `HHMods.QoL` | BepInPlugin | Hunger/thirst polish, empty-container tooltip, linked/filtered containers |
| `HHMods.Vehicles` | BepInPlugin | Gyroscope, Headlights, Tow bar, Collector, Impact Shielding |
| `HHMods.Weapons` | BepInPlugin | Proximity mine, Impact Grenade, Throwing axe, Nailgun |
| `HHMods.Recipes` | BepInPlugin | Water, Gunpowder, Steel Chest recipe additions |

### 4.2 Cross-plugin dependencies

```
                    HHMods.Core
                        ▲
        ┌──────┬────────┼────────┬──────────┬───────┐
        │      │        │        │          │       │
    Minimap  Recipes  Perks    QoL     Vehicles  Weapons
                        │                          │
                        │ (soft) ──── Pitcher perk ┤
                        │
                        └── (soft) ──── Geologist layer ─── Minimap
```

Solid arrow = hard reference (compile-time). Dashed = soft/runtime: the plugin checks for the presence of the other via reflection or a null-safe API on `HHMods.Core`, so any subset loads without breaking.

### 4.3 Shared registry pattern

Every "adding something to a game data structure" lives behind a registry in `HHMods.Core`:

- `PerkRegistry.Register(TalentSkillDefinition)` — builds an `AllSkill` at runtime, wraps in `TalentSkill`, appends to `All_Skills_Set._SurviveSkills / _CraftSkills / _FightSkills`. Idempotent; on scene reload, re-registers.
- `RecipeRegistry.Register(RecipeDefinition)` — appends `PerIconData` to the correct `CraftItemData.perIconData` for the target `WorkbenchType`. Idempotent.
- `MinimapLayerRegistry.Register(LayerDefinition)` — an in-memory list `HHMods.Minimap` subscribes to. Handles the "layer added before minimap loaded" case via a lazy queue.

### 4.4 Save/load convention

Each plugin persists its state to `Human Host_Data/Save/Save_XX/mod_<pluginId>.json`. `HHMods.Core.ModSidecar<T>` handles atomic write, versioning, and load fallback. Game's `SaveDataManager.Save_All_StorageBox_Data` and slot-load events are the sync points — hook postfix on both.

### 4.5 Load order

`HHMods.Core` is a `[BepInPlugin]` with a dummy `Awake` so BepInEx guarantees it loads first. Every other plugin declares `[BepInDependency("io.hh.core", BepInDependency.DependencyFlags.HardDependency)]`.

## 5. Confidence tiers

Each mod is classified by how much of its implementation is unblocked by verified game hooks.

### 5.1 Tier 1 — Fully verified, safe to ship

Hook points are known, method signatures dumped, no significant unknowns.

| Mod | Primary hook |
|---|---|
| Minimap | `World_Map_Mgr._CompassPro` + `CompassProPOI.RegisterPOI()` |
| Geologist perk | `Skill_Mgr._MinerSoundMats` scan + `MinimapLayerRegistry` |
| Panning Out perk | Harmony postfix on `Terrain_Dig.Dig_Terrain` |
| Alchemist perk | Harmony postfix on `Craft_Items.Click_Craft_Button` (Biochemical + Campfire workbenches) |
| Metallurgist perk | Same as Alchemist but on Furnace workbench |
| Pitcher perk | Runtime factor field on `Char_Skills`, consumed by Throwing axe |
| Water recipe | `RecipeRegistry.Register` targeting `Campfire` |
| Gunpowder recipe | `RecipeRegistry.Register` targeting `BiochemicalWorkbench` |
| Steel Chest | `RecipeRegistry.Register` + reuse existing container Build_Info |
| Color-coded hunger/thirst | Harmony postfix `Char_Status.Update_Food_UI` / `Update_Water_UI` |
| Empty container tooltip | `Loot_Mgr.No_Items_Inside(belongBIkey)` — method already exists |
| Gyroscope | Harmony postfix `Top_Info.Calculate_Mass_Center` |
| Nailgun (repair AoE) | `Tool_Interacter.Repair_Once(Build_Info)` postfix — existing repair pipeline |
| Impact Shielding | Harmony prefix `Battle_Info.MinusHP(float)` — damage entry point |

### 5.2 Tier 2 — Verified pattern, minor unknowns

Hook approach is proven for adjacent mods; one to two open details remain.

| Mod | Approach | Open detail |
|---|---|---|
| Headlights | Vehicle-attached Unity `Light` + custom MonoBehaviour + UI panel | Anchor to `Build_Info` transform; UI wiring convention |
| Proximity mine | Subclass `Trap_Base` (or clone `Trap_SensorSpike`); register with `Trap_Mgr` | `Trap_Mgr` registration path for custom traps |
| Impact Grenade | Throwable projectile mimicking existing bow throw path in `Weapon_Range.Get_Bow_Special_Info` | Explosion + area damage via existing damage pipeline |
| Throwing axe | Same as Grenade + recall via animation event; consumes `Pitcher` perk factor | Bow-style aim mechanic reused |
| Collector | Vehicle attachment MonoBehaviour + periodic sphere query → auto-insert via `Loot_Mgr` | Power drain hook (`Car_Control.Wheels_Control` neighborhood) |

### 5.3 Tier 3 — Novel implementation, higher risk

Existing game systems don't map 1:1; requires custom infrastructure.

| Mod | Risk | Mitigation |
|---|---|---|
| Tow bar | Unity `HingeJoint` / `ConfigurableJoint` between two vehicle roots. Save/load extension. Behavior under vehicle physics stress | Ship as an experimental feature flag; stress-test with 2-vehicle assemblies before extending |
| Filtered containers | Needs per-container tag filter state; extends `Loot_Mgr` container data model | Use `Icon_Info._Tags` (confirmed present) as tag source; store filter set in mod sidecar keyed by `belongBIkey` |
| Linked containers | Unifies inventory display across multiple `belongBIkey`s; intercept `Loot_Mgr.Enable_Loot_Window` | Present a virtual "combined" `Data_Slots_All` for the group; write-back splits by original container. Complex enough it may become v2 |

## 6. Dependency graph

```
CORE (all)
  │
  ├── Minimap (no deps beyond Core)
  │
  ├── Recipes ──► Water, Gunpowder, Steel Chest
  │
  ├── Perks
  │     ├── Alchemist       (independent)
  │     ├── Metallurgist    (independent)
  │     ├── Panning Out     (independent)
  │     ├── Pitcher         (independent; consumed by Weapons.ThrowingAxe)
  │     └── Geologist ─────► needs Minimap.LayerService (soft dep)
  │
  ├── QoL
  │     ├── HungerThirstPolish  (independent)
  │     ├── EmptyContainerTooltip (independent)
  │     ├── FilteredContainers   (uses Icon_Info._Tags)
  │     └── LinkedContainers     (depends on FilteredContainers concept)
  │
  ├── Vehicles
  │     ├── Gyroscope       (independent)
  │     ├── Headlights      (independent)
  │     ├── ImpactShielding (independent)
  │     ├── Collector       (independent; benefits from Filtered/Linked containers)
  │     └── TowBar          (independent)
  │
  └── Weapons
        ├── ProximityMine   (independent)
        ├── ImpactGrenade   (independent)
        ├── ThrowingAxe ────► consumes Pitcher perk (soft)
        └── Nailgun         (independent)
```

## 7. Delivery waves

Ordered to maximize early wins and build shared infrastructure before it's needed.

### Wave 1 — Foundation (this session)
- `HHMods.Core` full plumbing.
- `HHMods.Minimap` v1: toggle, layer registry, Player Waypoints + Merchants layers.
- `HHMods.Recipes` v1: Water, Gunpowder, Steel Chest.
- Rationale: pure additions; validates registry patterns; delivers immediate user value.

### Wave 2 — Perks
- All 5 perks. Geologist plugs into the Minimap layer service.
- Validates `PerkRegistry` at real scale.

### Wave 3 — QoL polish
- Hunger/thirst polish + empty-container tooltip. Both are Harmony one-liners.

### Wave 4 — Vehicle mods (part 1)
- Gyroscope (verified hook) + Impact Shielding (verified hook).

### Wave 5 — Weapons
- Nailgun + Proximity mine (Tier 1-adjacent) first, then Grenade + Throwing axe.

### Wave 6 — Vehicle mods (part 2)
- Headlights, Collector, Tow bar (Tier 2/3).

### Wave 7 — Advanced containers
- Filtered containers first (medium risk), Linked containers second (highest risk, may spill to v2).

## 8. Open questions

1. **Save-slot uninstall behavior.** If a player uninstalls `HHMods.Perks` mid-playthrough, the persisted perk levels in the sidecar become orphaned. Do we delete the sidecar on uninstall, or leave it in case they reinstall?
2. **Hunger/thirst polish visual style.** Sheet says "top left" — is that a fixed screen anchor or should we honor existing anchors and just retint/resize?
3. **Impact Shielding — one-time or refresh?** Does the shielding degrade over hits, or is it a permanent block property until the block itself is destroyed?
4. **Tow bar — how many bars per vehicle?** Physics may go unstable past 3–4 chained pivots.
5. **Collector — power draw model.** The sheet says "double the power from the engine as a wheel." Need to inspect `Car_Control.Wheels_Control` to understand vehicle power accounting.

## 9. Risks

- **Game updates may break Harmony patches.** Mitigation: keep patches surface-area small; version each plugin's known-good game version in its config.
- **Save format changes could invalidate sidecars.** Mitigation: `ModSidecar<T>` includes `SchemaVersion`; on mismatch, fall back to defaults with a Notify.
- **Multi-plugin loading edge cases.** BepInEx `[BepInDependency]` handles ordering, but transitive load failures need to be observable — Core exposes a `LoadReport` that any plugin can query at Start.
- **Tier 3 mods may prove infeasible.** If Linked Containers can't cleanly virtualize the data model, downgrade to a "quick-transfer between adjacent containers" UI helper.

## 10. Appendix — Verified hook points

Reference for implementation. All names verified via Mono.Cecil against shipped DLLs.

| System | Class::Method |
|---|---|
| Central hub | `Mgr_Hub._ins` (holds refs to every manager) |
| Skills | `Skill_Mgr._ins`, `All_Skills_Set._SurviveSkills`/`_CraftSkills`/`_FightSkills`, `Skill_Data.Get_Learned_Skill_Lv(string)`, `Char_Skills` (dozens of `_xxx_Factor` fields) |
| Mining | `Terrain_Dig._ins`, `Terrain_Dig.Dig_Terrain(RaycastHit, bool, bool)` |
| Loot | `Loot_Mgr._ins`, `Loot_Mgr.Enable_Loot_Window(...)`, `Loot_Mgr.No_Items_Inside(string)` |
| Crafting | `Craft_Mgr._ins`, `Craft_Items._CraftItemsData: CraftItemData[]`, `PerIconData` shape |
| Terrain materials | `Skill_Mgr._MinerSoundMats: Sound_Mat[]` (ore/rock definitions) |
| Vehicle COM | `Top_Info.Calculate_Mass_Center` |
| Vehicle physics transition | `Car_BuildMode_Switch.Vehicle_Switch_To_NoKinematic` |
| Vehicle Lua API | `Car_Coding.LoadPlayerCode(string)`, `Accelerate/Reverse/Steer_*`, `Rotate/Lock_Rot/Set_Rot_Angle` |
| Compass/minimap | `CompassNavigatorPro.CompassPro` (200+ props), `CompassProPOI.RegisterPOI()`, `World_Map_Mgr._CompassPro` |
| Traps | `Trap_Base` (base MonoBehaviour), `Trap_Mgr._ins` |
| Weapons | `Tool_Interacter` (base), `Weapon_Melee` / `Weapon_Range`, `Tool_Interacter.Repair_Once(Build_Info)` |
| Damage | `Battle_Info.MinusHP(float)` (single damage entry point) |
| Items/tags | `Icon_Info._Tag: string`, `_Tags: string[]`, `_SlotType: Slot_Type` |
| Hunger/thirst | `Char_Status._currentFood`, `_currentWater`, `_maxFood`, `_maxWater`, private `Update_Food_UI()`, `Update_Water_UI()` |
| Hotkeys | `Player_HotKeys._ins`, per-action `Hotkey_Sets` |
| Notifications | `NotificationSystem` |
| Save | `SaveDataManager`, `Save_Player_Data`, EasySave3 |
