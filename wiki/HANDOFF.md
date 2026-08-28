# Human Host Wiki — Handoff

**Wiki:** https://human-host.fandom.com/
**Source repo:** `c:\Users\SMC\HumanHostMods\wiki\`
**Game version:** Human Host (Virtual Matrix Studio), Unity 2022.3.62f3, Steam app 2393970
**Last session end:** 2026-08-28
**Status:** Feature-complete for Wave 1 (icons, item catalog, recipes, cross-links); Wave 2+ open work listed at the bottom

---

## 1. What's live on the wiki right now

**Pages** — around 315 total, all reachable from the [[Human host Wiki]] home page via the Nav template:

| Category | Count | Where |
|---|---|---|
| Overview pages (hand-authored) | 14 | Getting Started, Combat, Crafting, Building, Biomes, Perks, Vehicles, Weapons, Items, Food and Water, Mining, Modding, Controls, home page |
| Biomes (stub, cross-linked) | 10 | Mossy Forest, Mountain Forest, Tropical Jungle, Tropical Swamp, Desert, Desert Rocky, Winter Forest, Winter Town, Warzone, Wasteland |
| Locations / loot crates | 17 | 11 POIs (Airport, Mall, Diner, Pool, School, AV House, Country House I/II, Factory Zone, Warzone Buildings, CBU Building Pack) + 6 loot crates |
| Workbenches with full recipes | 11 | Anvil, Biochemical, Campfire, Carpentry, Cement Mixer, Cutting, Electronics, Furnace, Gun, HandMade, Mechanical |
| Traps | 15 | Every BT_* asset variant |
| Perks (factor-derived) | 23 | One per `_xxx_Factor` field on `Char_Skills` |
| Vehicle biome groups | 6 | One per `cars_<biome>` bundle |
| Ore / mineral | 16 | Every BO_* mineable asset |
| Food + water | 13 | Cactus, Mushroom, Pine Cone, 5 canned foods, Sausage, Bottled Water, Fruit Juice, Soda, Energy Drink |
| Melee weapons | 20 | Axes, spears, knives, blunt |
| Ranged + ammo + optics | 32 | Firearms, ammo boxes, bows, arrows, scopes/sights |
| Atomic materials (from recipes) | 211 | Wood, Stone, Charcoal, Forged metals, Nails, Gear, Circuit Board, Medical items, EQI armor, VPI vehicle parts, tier building materials, etc. |
| Recipes hub (alphabetical index) | 1 | Recipes |
| Mods | 8 | Mods hub + HHMods suite pages |
| Templates | 9 | Nav, Stub, Recipe + 6 Infoboxes |
| Redirects | 3 | Spike → IronSpike Trap, Rot Blade → Rotating Blade, Laser → Laser Trap |

**Images** — 392 icon PNGs live in the File namespace, sourced from the game's own icon bundles. Every per-item stub with a known icon references it in the infobox `image =` field so pages render visually.

**Recipes** — 745 raw recipes across all 10 workbench prefabs (WB_Campfire, WB_Anvil, etc.), extracted and cross-linked. On workbench pages they're consolidated by (output + input types + craft time), collapsing tier-shape variants: Cutting Workbench went from 558 raw rows to 38 unique rows with min-max ingredient ranges + a "Variants" column showing shape count. Every material page has "Crafted at" and "Used in" tables that back-reference the full recipe graph.

---

## 2. Everything lives under `wiki/`

```
wiki/
├── HANDOFF.md              ← this file
├── README.md               ← original wiki structure + workflow overview
├── data/                   ← extractor output (checked in for review)
│   ├── recipes.json          745 recipes, workbench + inputs/outputs + times
│   ├── guid_name_map.json    6,347 Unity Addressables GUIDs → canonical asset names
│   ├── asset_catalog.json    Bucketed asset catalog by prefix (ores, foods, weapons, etc.)
│   ├── skill_factors.json    23 Char_Skills factor fields
│   ├── managers.json         56 singleton managers
│   ├── workbench_types.json  11 WorkbenchType enum values
│   ├── slot_types.json       7 Slot_Type enum values
│   ├── biomes.json / locations.json / loot_crates.json / vehicle_bundles.json
│   ├── trap_classes.json / weapon_classes.json / build_categories.json
│   ├── resource_catalog.json ← Cecil scan for Sound_Mat + resource types
│   └── recipe_shapes.json    ← Cecil type-shape info for Craft_Items / Build_Info
│
├── extractor/              ← .NET 8 Cecil-based extractor (16 sub-extractors)
│   ├── HHWiki.Extractor.csproj
│   ├── Program.cs
│   ├── CecilLoader.cs
│   ├── Extractors/
│   │   ├── BiomeExtractor.cs / LocationExtractor.cs / LootCrateExtractor.cs
│   │   ├── VehicleBundleExtractor.cs / SkillFactorExtractor.cs / TrapExtractor.cs
│   │   ├── WeaponExtractor.cs / BuildCategoryExtractor.cs / WorkbenchExtractor.cs
│   │   ├── SlotTypeExtractor.cs / ManagerExtractor.cs / ResourceCatalogExtractor.cs
│   │   ├── CatalogExtractor.cs        ← bucketed prefix scan of catalog.json
│   │   ├── CatalogEntryParser.cs      ← proper Unity Addressables binary decoder
│   │   └── RecipeExtractor.cs         ← Cecil type-shape scan
│   ├── Parse-Recipes.ps1              ← YAML parser for workbench prefabs → recipes.json
│   ├── README.md
│   └── .gitignore (bin/ obj/)
│
├── generator/
│   └── Generate-Pages.ps1  ← turns data/*.json into pages/**/*.mediawiki stubs
│
├── uploader/
│   ├── Upload-ToFandom.ps1 ← page uploader (action=edit, diff-aware, retry-with-backoff)
│   ├── Upload-Assets.ps1   ← file uploader (action=upload, multipart POST)
│   ├── config.json         ← Fandom bot credentials (GITIGNORED)
│   ├── config.example.json ← template
│   ├── README.md           ← bot password setup + workflow
│   └── .gitignore (config.json)
│
├── templates/              ← 9 MediaWiki Template: pages (Nav, Stub, Recipe, 6 Infoboxes)
├── pages/                  ← MediaWiki source (~315 pages across subfolders)
│   ├── (top-level overview pages)
│   ├── biomes/ / locations/ / crafting/ / building/ / perks/ / vehicles/ / mods/
│   └── items/{ores,food,water,melee,ranged,materials}/
└── assets/                 ← GITIGNORED, ~7 GB
    ├── raw/all_icons/          AssetRipper output of all icon bundles (956 PNGs + prefabs)
    ├── raw/main/               AssetRipper output of main bundles (workbench prefabs + textures + meshes)
    ├── curated/icons/          394 primary item icons ready to upload
    └── *.log                   push run logs
```

---

## 3. The pipeline (extract → generate → upload)

```powershell
# 1. Extract data from DLLs + Addressables catalog (fast, <5s)
dotnet run --project wiki/extractor -c Release -- `
    --game 'C:\Program Files (x86)\Steam\steamapps\common\Human Host' `
    --out wiki/data

# 2. Parse recipe prefabs (needs main-bundle extraction to have run first — see step 5)
pwsh wiki/extractor/Parse-Recipes.ps1

# 3. Generate MediaWiki stubs (~30s, produces / updates hundreds of files in wiki/pages/)
pwsh wiki/generator/Generate-Pages.ps1

# 4. Dry-run upload to see what would change
pwsh wiki/uploader/Upload-ToFandom.ps1

# 5. Commit
pwsh wiki/uploader/Upload-ToFandom.ps1 -Commit -DelayMs 3000
```

**One-time asset extraction (needed for icons + recipes):**

```powershell
$ARC = 'C:\Users\SMC\AppData\Local\Microsoft\WinGet\Packages\MeikoMei16.AssetRipperCLI_*\AssetRipper.Tools.ExportRunner.exe'
$B = 'C:\Program Files (x86)\Steam\steamapps\common\Human Host\Human Host_Data\StreamingAssets\aa\StandaloneWindows64'

# Icon bundles (~30s, produces PNGs and item prefabs)
& $ARC export (Get-ChildItem $B -Filter '*icon_assets_all*.bundle').FullName `
    --output wiki/assets/raw/all_icons --profile full-project

# Main bundles for workbench prefabs + textures (~90s, 6.9GB output)
& $ARC export (Get-ChildItem $B -Include 'build_function_assets_all*','build_ore_assets_all*','build_trap_assets_all*','melee_weapon_assets_all*','range_weapon_assets_all*','equipments_assets_all*','vehicle_parts_assets_all*' -Recurse).FullName `
    --output wiki/assets/raw/main --profile full-project

# Curate icons: filter to non-tier-variant primary items (~394 PNGs)
$src = 'wiki/assets/raw/all_icons/ExportedProject/Assets/Texture2D'
$dest = 'wiki/assets/curated/icons'
New-Item -ItemType Directory -Force $dest | Out-Null
Get-ChildItem $src -Filter *.png | Where-Object {
    $_.BaseName -notmatch '^[1-7]_[0-9]' -and $_.BaseName -notmatch '^(Albedo|Normal|Mask|Roughness|Metallic|AO_|MicroSplat|Splat)'
} | Copy-Item -Destination $dest

# Upload icons (~20 min at 3s/upload)
pwsh wiki/uploader/Upload-Assets.ps1 -FromDir wiki/assets/curated/icons -Commit -DelayMs 3000
```

Full details: [wiki/extractor/README.md](extractor/README.md) and [wiki/uploader/README.md](uploader/README.md).

---

## 4. Prerequisites on any new machine

| Tool | Purpose | Install |
|---|---|---|
| **.NET SDK 8+** | C# extractor | `winget install --id Microsoft.DotNet.SDK.8` |
| **AssetRipper CLI** | Icon + prefab extraction from bundles | `winget install --id MeikoMei16.AssetRipperCLI` |
| **PowerShell 5.1+** | Generator + uploader | Preinstalled on Windows |
| **Fandom bot password** | Wiki API auth | Special:BotPasswords on the wiki |

Bot password setup:

1. Log in to https://human-host.fandom.com/ with your Google account
2. Visit https://human-host.fandom.com/wiki/Special:BotPasswords
3. Create a bot named `HHWikiBot`; grant "High-volume editing", "Edit existing pages", "Create pages"
4. Copy the compound username + generated password into `wiki/uploader/config.json` (schema in `config.example.json`)

The existing bot password is stored in `wiki/uploader/config.json` on this machine. That file is gitignored. When moving to a new machine, either:
- Copy `config.json` across manually (out-of-band, since it's not in git), OR
- Create a new bot password on the wiki and use that

---

## 5. Data-model notes worth knowing

**Recipe extraction has three moving parts** and they matter if you're debugging:

1. **AssetRipper** exports the game's Unity project. Workbench prefabs (WB_Campfire, WB_Anvil, etc.) live under `wiki/assets/raw/main/ExportedProject/Assets/In_Use/Conts/Mods/Player_Made/Workbench/*.prefab`. These YAML files hold the `_CraftItemsData → perIconData → matsData` structure that defines every recipe.

2. **`CatalogEntryParser.cs`** decodes Unity Addressables' `catalog.json` binary format (base64-encoded `KeyDataString + EntryDataString + BucketDataString`). It maps every asset GUID (like `55fa6db8ca4cb514a9dbbe06649807b2`) to its canonical readable name (like `BFI_BurningFire_Icon`). Without this, recipes are unreadable — the prefab YAML references assets by GUID only.

3. **`Parse-Recipes.ps1`** walks each workbench prefab, extracts each `perIconData` entry (output GUID + craft count + craft seconds + list of ingredient GUIDs + counts), and looks each GUID up in `guid_name_map.json`. Emits `data/recipes.json` — 745 recipes with names.

**Recipe consolidation** (the most recent addition):
- Recipes are grouped by signature = `(output_name, output_qty, craft_seconds, sorted input names)` — quantities are the varying axis
- Each group becomes one row with ingredient min-max range + a "Variants" cell showing how many raw recipes were folded together
- This is why Cutting Workbench shows "18 shapes" per row: each material's 18 tier-shape build variants collapse to one recipe entry

**Cross-references** (`Build-CrossRefSections` in Generate-Pages.ps1) build two indices from recipes.json at generator startup:
- `$ItemProducedBy[title]` — list of {workbench, output_qty, craft_seconds, inputs} for every recipe outputting this item
- `$ItemUsedIn[title]` — list of {workbench, output_name, output_qty, need_count} for every recipe consuming this item

Both indices are consolidated the same way as the workbench tables before rendering.

**Tier-variant collapse**: `1_1_Block_Damaged_Planks`, `1_1_Cuboid_Damaged_Planks`, `1_1_Triangle_Damaged_Planks`, etc. all normalize to `Damaged Planks` via `Normalize-RecipeItemName` in Generate-Pages.ps1. Tier prefix pattern: `^[1-7]_[1-9]_`. Shape tokens (Block, Cuboid, Half_Cylinder_L, Triangle_CornerS, etc.) are stripped, then remaining underscores → spaces.

---

## 6. Known caveats + edge cases

- **Fandom filetype allowlist blocks FBX/OBJ.** Only png/gif/jpg/webp/svg/pdf/audio/video accepted. 3D model previews would need to be rendered to PNG (Unity or Blender pipeline) before upload. Unity 2022.3.62f3 is installed at `C:\Program Files\Unity 2022.3.62f3\` in case you want to pursue this later.
- **Recipe name pairing is ~99% accurate** thanks to the proper `CatalogEntryParser`. A handful of entries with no readable alias may show a raw GUID or a "BFI_*" prefix. Every workbench page carries an inline caveat inviting hand-edits.
- **One shared "BurningFire" recipe** appears at both Campfire and Furnace as their first entry (1200s craft time, needs Wood x10). This is real game data — likely a shared workbench-lit fire asset — not a parser bug.
- **The recipe consolidation "Variants" column** shows how many raw recipes were folded together. Rows with "1" have no tier-shape variance; rows like "18 shapes" span every building-shape variant of that material.
- **Rate limits.** Fandom returns `[ratelimited]` if you push too fast. The uploader has 30/60/90s backoff built in; run with `-DelayMs 3000` (default) or higher if pushing hundreds of edits. Icon uploads seem to be even more sensitive than page edits — same delay works but longer is safer.
- **`ClothSack.png` exceeds Fandom's 10MB per-file limit** and was the only skipped upload. Not a critical item.
- **The extracted assets folder (`wiki/assets/raw/`, ~7GB) is gitignored.** Regenerable via AssetRipper. Delete anytime.

---

## 7. Where to hand-edit vs regenerate

Every generated page starts with:
```
<!-- HHWIKI:GENERATED: remove this line to protect from regeneration -->
```

**Delete that first line** to protect a page from being overwritten by future `Generate-Pages.ps1` runs. The uploader also uses this marker to know whether a page is safe to touch.

**Pages that are always hand-authored** (never regenerated):
- All 14 overview pages under `wiki/pages/` root (Getting_Started, Crafting, Building, Biomes, Perks, etc.)
- All 8 mod pages under `wiki/pages/mods/`
- The 3 redirect pages (Spike, Rot_Blade, Laser)

**Pages that get regenerated** (safe to edit unless you strip the marker):
- Everything under `pages/biomes/`, `pages/locations/`, `pages/crafting/`, `pages/perks/`, `pages/vehicles/`, `pages/building/`, `pages/items/**/`
- The `pages/Recipes.mediawiki` hub

---

## 8. Open work (Wave 2+)

**High-value, extractable:**
1. **Extend BiomeExtractor** to include real ore/creature/POI mappings per biome (biome→resources table) — currently pages only list what's inferable from bundle names.
2. **Individual weapon stat pages** — pull damage/durability/ammo per weapon from prefabs (similar approach to Parse-Recipes.ps1 but on Weapon_Melee / Weapon_Range prefabs).
3. **Per-item stack sizes and tags** — each Icon_Info prefab has `MaxStack`, `_Tag`, `_Tags`, `_SlotType`. Parse and add to material infoboxes.
4. **Localization pass** — `Language.dll` and `Language_Mgr` singleton hold i18n strings. Cecil could pull English display names to replace the raw asset IDs used as page titles.

**High-value, needs in-game observation:**
5. **11 POI pages** need loot tables, layout notes, strategy sections (Airport, Mall, Diner, Pool, School, Country House I/II, AV House, Factory Zone, Warzone Buildings, CBU Building Pack)
6. **11 workbench pages** need build costs (materials to place the workbench) and unlocked-by prerequisites
7. **6 loot crate pages** need loot table observation
8. **10 biome pages** need climate/threat detail, wildlife (when added), specific ore distributions

**Nice-to-have, more effort:**
9. **3D model preview PNGs** — install Unity Editor 2022.3.62f3 (already installed), open the AssetRipper-extracted project, write an Editor script that renders each item mesh to an isometric PNG on transparent background, batch-render ~150 primary models, upload PNGs, wire into pages. Estimated 2-3 hours.
10. **Vehicle chassis pages** — extract `cars_<biome>` bundle contents and generate per-chassis pages. Currently we have per-biome vehicle groups but no per-chassis detail.
11. **Interactive map** — Fandom supports interactive maps; the Human host Wiki home page has a placeholder `<interactive-map name="Example map" />`. Would need biome layouts + POI coordinates.

**Cleanup:**
12. **`WIP` and `Recipe Sack` recipes** are placeholder / game-internal items still showing in recipe tables — could be filtered out of Generate-Pages.ps1 output.
13. **Duplicate output rows** in some workbench recipe tables where the game intentionally has two paths to the same output (e.g. Forged Iron from Scrap Iron OR from Iron Ore) — these correctly render as two rows but could be visually grouped better.
14. **Redirects for common alternate names** — could add "Wood Plank" → "Plank", "Ammo Box" → each caliber's ammo box page, etc.

---

## 9. Environment reference

**Installed on this machine:**
- .NET SDK 8.0.424 at `C:\Program Files\dotnet\`
- .NET Runtime 10.0.11 at `C:\Users\SMC\AppData\Roaming\Code\User\globalStorage\ms-dotnettools.vscode-dotnet-runtime\.dotnet\10.0.11~x64\` (installed by VS Code)
- AssetRipper 2.0.0 GUI at `C:\Users\SMC\AppData\Local\Microsoft\WinGet\Packages\AssetRipper.AssetRipper_*\`
- AssetRipper CLI 0.1.0 at `C:\Users\SMC\AppData\Local\Microsoft\WinGet\Packages\MeikoMei16.AssetRipperCLI_*\`
- Unity Editor 2022.3.62f3 at `C:\Program Files\Unity 2022.3.62f3\` (installed but unused so far; kept for optional 3D render pipeline)
- BepInEx 5.4.23.2 at `<game>\BepInEx\` (game shipped with this preinstalled; used by the HHMods suite)

**Fandom bot credentials** in `wiki/uploader/config.json` — never checked into git. If moving machines, either copy this file across manually or create a fresh bot password.

**Game path** hardcoded in a few extractor scripts: `C:\Program Files (x86)\Steam\steamapps\common\Human Host`. If the game moves, update these:
- `wiki/extractor/Parse-Recipes.ps1` — the `-CatalogPath` default
- `wiki/uploader/README.md` — the workflow examples
- `wiki/extractor/README.md` — the setup instructions

Or override at invocation with the appropriate `-Game` / `-CatalogPath` flag.

---

## 10. Companion documentation

- [wiki/README.md](README.md) — original wiki structure design and Fandom mapping conventions
- [wiki/extractor/README.md](extractor/README.md) — pipeline reference with full setup script
- [wiki/uploader/README.md](uploader/README.md) — Fandom uploader setup + workflow
- [docs/DESIGN.md](../docs/DESIGN.md) — the HHMods (BepInEx plugin suite) design doc, which is orthogonal to the wiki but shares the same reverse-engineered game knowledge base
