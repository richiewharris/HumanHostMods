# HHMods.Perks — Rebuild Specification

**Status:** Draft v1
**Author:** SMC + Claude
**Last updated:** 2026-09-01
**Supersedes:** the initial `HHMods.Perks` shipping in `bin/Release` (config-only Char_Skills clobber)
**Depends on:** `HHMods.Core`, and (soft) `HHMods.Minimap` for the Geologist layer

---

## 1. Overview

The current `HHMods.Perks` module is a set of 14 BepInEx sliders that write directly to `Char_Skills` modifier fields on the local player. Runtime inspection of `Skill_Mgr._SkillSets` confirms the game already ships those same fields as authored perks (Miner, Lumberjack, Scavenger, Melee Master, and so on), so our sliders silently clobber the values the player earns from the game's own perk tree. That behavior is being retired.

The rebuild reframes `HHMods.Perks` as a package that injects **new** `TalentSkill` entries into the game's existing perk panel. Five new perks are shipping in v1, matching the original design doc:

| Perk | Category | Ranks | Effect |
|---|---|---|---|
| Pitcher | Combat | 5 | +5 yards throwing range per rank |
| Panning Out | Survival | 10 | 10% chance per rank to earn an extra mining drop equal to 50% of the mined resource; capped by biome ore inventory |
| Geologist | Survival | 10 | +10 yard ore-node reveal radius per rank on both minimap and world map |
| Alchemist | Craft | 10 | 5% chance per rank to recover ingredients when crafting at Biochemical Workbench or Campfire |
| Metallurgist | Craft | 10 | 5% chance per rank to recover ingredients when crafting at Furnace |

All five perks appear in the game's native perk panel with icons, tooltips, XP-gated selection, and save persistence. There is no separate UI. Effects are additive/multiplicative on top of the game's own perks and never overwrite `Char_Skills` fields.

## 2. Goals

- Retire the current `Char_Skills`-clobbering config-slider implementation cleanly (existing config file retained, values ignored, deprecation logged).
- Inject the five perks into the game's `_FightSkills` / `_SurviveSkills` / `_CraftSkills` arrays such that they appear in the panel indistinguishable from native perks.
- Reuse the game's `Skill_Data._Learned_Skills` list for state so save/load requires no sidecar.
- Reuse game sprites where possible for thumbnails; leave a file-loader hook for external art overrides.
- Each perk's effect implementation is isolated behind a Harmony patch so one perk's bugs don't cascade.

## 3. Non-Goals

- No custom perk UI — we use what the game already renders.
- No modification of existing perk balance — v1 adds only. Miner still does what Miner does.
- No cross-perk synergy or set bonuses in v1.
- No multiplayer coordination — game appears single-player.
- No auto-migration from old sliders to new perk ranks. Old sliders quietly stop applying; player earns new perks fresh.

## 4. Architecture

### 4.1 New service: `PerkInjector`

Lives in `HHMods.Perks`. Called once on `GameEvents.HubReady`:

1. Resolve `Skill_Mgr.ins._SkillSets` via reflection (nested `TalentSkill` type is `NestedAssembly`).
2. For each perk definition, construct a fresh `AllSkill` instance:
   - `_name`, `_instruct`: build a `Language_Text` with English + a couple fallback locales.
   - `icon`: sprite from either (a) file loader at `plugins/HHMods/perks/<PerkId>.png`, or (b) game-sprite lookup by name from `Resources.FindObjectsOfTypeAll<Sprite>()`, or (c) procedural fallback.
   - `maxLv`: from the perk def.
   - `isBuff = false`, `buffPeriod = 0`, `stackableBuff = false` — none of ours are buffs.
   - `_values`: one `SkillValue` per rank with `_replaceStrs` populated so the `[ * ]` placeholders in the description render correctly.
3. Wrap the `AllSkill` in a `TalentSkill` with the correct class label.
4. `Array.Resize` the target `_FightSkills` / `_SurviveSkills` / `_CraftSkills` array by +1 and slot in the new entry.
5. Idempotent: on scene reload the injector re-checks by perk name and skips already-present entries.

### 4.2 Per-perk effect application

The game reads `Skill_Data._Learned_Skills` (a `List<LearnedSkill>` of `{ skillName, currLv }`) to know what the player has. Each perk's effect module:

- Reads its current rank via a helper `PerkRank(string perkName)` that walks `_Learned_Skills`.
- Applies the effect via one Harmony patch, scoped to the exact function the effect belongs on.
- If rank is 0, patch does nothing — no runtime cost.

### 4.3 Load order and dependency

`HHMods.Perks` depends on `HHMods.Core` (hard). Geologist has a **soft** dependency on `HHMods.Minimap` — checks at runtime via reflection whether the minimap is loaded, and only registers the ore-node layer if so. The other four perks have no plugin-cross dependencies.

## 5. Perk specifications

### 5.1 Pitcher (Combat, 5 ranks)

**Description string:** `Increases throwing range by [ * ] yards.`

**Per-rank values:**
| Rank | +Yards |
|---|---|
| 1 | 5 |
| 2 | 10 |
| 3 | 15 |
| 4 | 20 |
| 5 | 25 |

**Implementation:**
- Discovery task: locate throw-distance calculation. Search Assembly-CSharp + Player.dll for classes matching `Throwable_*`, or hooks like `Player_Input.Throw*`. Likely a method that scales a base velocity by grip strength / stamina.
- Harmony patch: postfix on that method, add `rank * 5` yards worth of velocity or range multiplier.
- If the throw physics use a distance parameter rather than a velocity, prefer patching the distance calc; otherwise scale velocity by `sqrt((baseRange + bonus) / baseRange)`.
- Icon candidate: reuse `Archer` or `Wild Hunter` sprite; fallback to procedural silhouette.

### 5.2 Panning Out (Survival, 10 ranks)

**Description string:** `[ *% ] chance to earn an extra drop equal to 50% of the mined resource. Limited by biome ore availability.`

**Per-rank values:**
| Rank | Chance % |
|---|---|
| 1 | 10 |
| 2 | 20 |
| … | … |
| 10 | 100 |

**Implementation:**
- Discovery task: find where mining drops are generated. Likely `Terrain_Dig` (already exposed via `MgrHub.TerrainDig`) has an `OnHit` or `Drop_Materials` method that spawns loot at the hit point.
- Harmony patch: postfix on the drop method. If the hit was on a mineable node, roll `Random.value < rank * 0.1`. On success, invoke the same drop with quantity multiplied by 0.5 (rounded up, floor 1).
- Biome cap: check the biome's ore inventory (discovery on where that lives — probably a table keyed by biome or ore-node prefab). If the biome doesn't contain this ore, suppress the extra drop.
- Icon candidate: reuse `Miner` sprite recolored, or find a gem/nugget sprite in the game's asset pool.

### 5.3 Geologist (Survival, 10 ranks)

**Description string:** `Reveals ore nodes within [ * ] yards on both minimap and world map.`

**Per-rank values:**
| Rank | Radius (yards) |
|---|---|
| 1 | 10 |
| 2 | 20 |
| … | … |
| 10 | 100 |

**Implementation:**
- Discovery task: identify the component / tag that marks an ore node in the world. Candidates: a `Ore_*` prefab, a `Terrain_Dig` region with a specific type flag, or a specific layer.
- Reuses `MinimapV2Controller`'s existing POI overlay pipeline. Register a new POI source that scans for ore nodes within `PerkRank("Geologist") * 10` yards of the player each frame, projects to map pixels, and renders as icons with a distinct color per ore type.
- Soft plugin dependency: only wires up if `HHMods.Minimap` is loaded. If not, perk still selects/ranks but has no visible effect (log a warning once).
- World map integration: hook `World_Map_Mgr.Add_POI` to inject ore nodes as CompassProPOIs while the world map is open, then remove on close.
- Icon candidate: reuse `Scavenger` sprite, or a magnifying-glass / compass icon.

### 5.4 Alchemist (Craft, 10 ranks)

**Description string:** `[ *% ] chance to recover ingredients when crafting at the Biochemical Workbench or Campfire.`

**Per-rank values:**
| Rank | Chance % |
|---|---|
| 1 | 5 |
| 2 | 10 |
| … | … |
| 10 | 50 |

**Implementation:**
- Discovery task: find the material-consumption call in the craft flow. In `Craft_Items` we already tracked `Transfer_Mats_From_Player_To_Workbench` and `Begin_Craft_Update`; one of them consumes the mats. May also be a separate `Consume_Materials` step.
- Harmony patch: prefix on the consumption call. Check `_workbenchType` is `BiochemicalWorkbench` or `Campfire`. For each material in the recipe, roll `Random.value < rank * 0.05`; on success, mark that material as "refund" and re-add the same quantity to the workbench crate (or player inventory) after the consume completes.
- Icon candidate: reuse `Artisan`, or find a flask/mortar sprite.

### 5.5 Metallurgist (Craft, 10 ranks)

**Description string:** `[ *% ] chance to recover ingredients when crafting at the Furnace.`

**Per-rank values:** identical to Alchemist.

**Implementation:** Same mechanism as Alchemist, restricted to `WorkbenchType.Furnace`. The two share a `MaterialRefundPatch` implementation parameterized by workbench type set — one Harmony patch handles both perks.

**Icon candidate:** reuse `Miner` recolored, or find an anvil sprite.

## 6. Cross-cutting concerns

### 6.1 Save/load

The game's own `SaveDataManager` persists `Skill_Data._Learned_Skills` as part of the character save. Our perks are stored in that same list under their own skill names, so save/load works transparently. Verification task at end of Milestone 1: earn Pitcher rank 3, save, quit, load, confirm rank still reads as 3.

### 6.2 Localization

`Language_Text._Infos` is a list of `LanguageTypeInfo { languageType, text }`. For v1 we ship English text only, but populate the enum for every locale the game supports (Cecil-enumerable at build time via `LanguageType` enum in `Language.dll`) with the English fallback. Non-English players see English until translations arrive.

### 6.3 Icon sourcing

Precedence order per perk:
1. **File override** — `BepInEx/plugins/HHMods/perks/<PerkId>.png` (32x32 or 64x64, alpha PNG). Loaded once at startup.
2. **Game sprite lookup** — case-insensitive name match against `Resources.FindObjectsOfTypeAll<Sprite>()`. Preferred targets are the existing perk sprites (`Miner`, `Archer`, etc.) that match the perk's theme.
3. **Procedural fallback** — a 32x32 white-on-black silhouette generated by our existing texture-drawing helpers. Not stylistically consistent with the game's B&W character illustrations, but functional and clearly marks the perk as ours.

For v1 we ship with only #2 and #3 wired. The file loader is stubbed for future artist commissions.

### 6.4 Deprecation of the old sliders

- Rename `HHMods.Perks.Plugin` config section headers to include a `[DEPRECATED]` prefix.
- Log one warning on first Awake: `"[Perks] Legacy config sliders are no longer applied. See docs/PERKS_SPEC.md for the new perk model."`
- Do not touch `Char_Skills` anymore. Leave the config file so users don't lose settings, but ignore the values.
- `PerkDumper` stays as a diagnostic tool (invoked on demand, not on every apply).

## 7. Milestones

### Milestone 1: Injection PoC + Pitcher
Deliverable: Pitcher appears in the Combat tab. Player selects it, ranks it up 1→5, throws farther per rank. Rank persists across save/load.

Tasks:
1. Retire the current `Char_Skills`-clobber path (remove `ApplyToLocalPlayer` calls; keep dumper).
2. Build `PerkInjector` service.
3. Build shared `PerkDefinition` type and rank-reading helper.
4. Discovery on throw distance code.
5. Wire Pitcher patch.
6. Verify UI + save/load.

**Blockers to unblock:** location of throw distance calculation.

### Milestone 2: Panning Out
Deliverable: Panning Out appears in Survival tab. Mining a stone/ore node has a chance to yield a bonus drop.

Tasks:
1. Discovery on mining drop code and biome ore inventory.
2. Wire the drop patch.
3. Verify UI + effect.

**Blockers:** identify the ore-drop hook and biome ore table.

### Milestone 3: Alchemist + Metallurgist
Deliverable: Both perks appear in Craft tab. Crafting at the right workbench types has a rank-based chance to preserve ingredients.

Tasks:
1. Fix the existing `HHMods.Recipes` reflection init failure (logged in the current session's LogOutput as "nested types not found") — required for hook access.
2. Discovery on material consumption call.
3. Build shared `MaterialRefundPatch` parameterized by workbench-type set.
4. Wire both perks against it.
5. Verify UI + refund behavior.

**Blockers:** the recipes-mod reflection fix, and locating material consumption.

### Milestone 4: Geologist
Deliverable: Geologist appears in Survival tab. Ranking it up adds ore nodes to both minimap and world map within an expanding radius.

Tasks:
1. Discovery on ore node component/tag.
2. Build ore-node POI source integrated with `MinimapV2Controller`'s overlay pipeline.
3. Hook `World_Map_Mgr` for the world-map layer.
4. Verify UI + reveal behavior at multiple ranks.

**Blockers:** ore node type identification.

## 8. Open questions

1. Does the game's perk panel auto-scale to arbitrary counts, or is it laid out for a fixed number of slots? If the latter, injecting a 6th Craft perk (Alchemist) or 16th Survival perk (Geologist / Panning Out) may cause layout overflow. Verify visually after Milestone 1.
2. What is the exact XP cost the game charges to unlock a rank of a new perk? If it's a fixed constant per rank across the whole tree, our 10-rank perks cost 2× a 5-rank perk which may be balance-inappropriate. If cost is per-perk configurable, we can tune it.
3. Does `Skill_Data._Learned_Skills` de-serialize skills whose names aren't in the game's `_name2*Skills` dictionaries at load time? If it discards unknowns, we need to ensure our perks are injected before save load runs. `GameEvents.HubReady` fires before the character finishes spawning per the current log evidence, so we're probably OK — but verify.
4. For Panning Out's "capped by biome ores available" — is that a per-node check (ore is either there or not) or a soft cap (chance reduced if ore is rare in this biome)? Original doc says "limited by ores available in the biome" — read as: if the biome has no gold nodes, Panning Out on a stone node cannot produce gold. So the bonus is always the same resource as the base drop, just at 50% quantity. Confirm with user before implementing.

## 9. Deliverables checklist

- [ ] `PerkInjector` service in `HHMods.Perks/PerkInjector.cs`
- [ ] `PerkDefinition` type + `PerkRank(string)` helper in `HHMods.Perks/PerkDefinition.cs`
- [ ] `PitcherPerk.cs` — patch + wiring
- [ ] `PanningOutPerk.cs` — patch + wiring
- [ ] `GeologistPerk.cs` — patch + minimap layer wiring
- [ ] `AlchemistPerk.cs` — patch + wiring
- [ ] `MetallurgistPerk.cs` — patch + wiring
- [ ] Deprecation notice on legacy `Plugin.cs` config sliders
- [ ] `docs/PERKS_SPEC.md` (this document)
- [ ] Verification: for each perk, screenshot showing it in the panel + tooltip + rank progression
