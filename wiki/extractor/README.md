# HHWiki Extractor / Generator / Uploader Pipeline

Full extraction-to-Fandom pipeline for the Human Host wiki. Every asset and page on [human-host.fandom.com](https://human-host.fandom.com) is derivable from the shipped game files.

## Pipeline overview

```
Game files                          Extractors                     Generator                Uploader
──────────                          ──────────                     ─────────                ────────
Assembly-CSharp.dll  ────Cecil────► data/skill_factors.json ──┐
                                    data/trap_classes.json    │
                                    data/managers.json        │
                                    data/workbench_types.json │
                                    ...                       │
                                                              ├──► pages/**/*.mediawiki ──► Fandom
StreamingAssets/aa/catalog.json ──► data/asset_catalog.json  ─┤    (via generator)          (via
                                    data/guid_name_map.json ─┤                              Upload-ToFandom.ps1)
                                     (proper Addressables    │
                                      binary decoder)        │
                                                              │
Icon bundles ────AssetRipper────►   assets/curated/icons/    │                          ──► Fandom Files
                                    (394 primary PNGs)        │                              (via
                                                              │                              Upload-Assets.ps1)
Main bundles ────AssetRipper────►   assets/raw/main/         ─┘
   (workbench prefabs)              WB_*.prefab (YAML)
                                        │
                                        ▼
                                    Parse-Recipes.ps1
                                        │
                                        ▼
                                    data/recipes.json
                                    (745 recipes)
```

## Setup (one-time)

1. **.NET SDK 8** (for the C# extractor): `winget install --id Microsoft.DotNet.SDK.8`
2. **AssetRipper CLI** (for icon and prefab extraction): `winget install --id MeikoMei16.AssetRipperCLI`
3. **Fandom bot password** in `wiki/uploader/config.json` (see `config.example.json`)

## Regenerating everything from scratch

```powershell
# 1. Extract from DLLs + catalog (fast, <1min)
cd wiki/extractor
dotnet build -c Release
dotnet run -c Release -- --game 'C:\Program Files (x86)\Steam\steamapps\common\Human Host' --out ../data

# 2. Extract icon PNGs from asset bundles (~30s)
$exe = 'C:\Users\<you>\AppData\Local\Microsoft\WinGet\Packages\MeikoMei16.AssetRipperCLI_*\AssetRipper.Tools.ExportRunner.exe'
$bundleDir = 'C:\Program Files (x86)\Steam\steamapps\common\Human Host\Human Host_Data\StreamingAssets\aa\StandaloneWindows64'
& $exe export (Get-ChildItem $bundleDir -Filter '*icon_assets_all*.bundle').FullName --output ../assets/raw/all_icons --profile full-project

# 3. Extract main bundles for workbench prefabs (~2 min, 6.9GB output)
& $exe export (Get-ChildItem $bundleDir -Filter '{build_function,build_ore,build_trap,melee_weapon,range_weapon,equipments,vehicle_parts}_assets_all_*.bundle').FullName --output ../assets/raw/main --profile full-project

# 4. Parse recipes from workbench prefabs
pwsh ../extractor/Parse-Recipes.ps1

# 5. Curate icons (skip building-tier variants)
$src = '../assets/raw/all_icons/ExportedProject/Assets/Texture2D'
$dest = '../assets/curated/icons'
New-Item -ItemType Directory -Force $dest | Out-Null
Get-ChildItem $src -Filter *.png | Where-Object {
    $_.BaseName -notmatch '^[1-7]_[0-9]' -and $_.BaseName -notmatch '^(Albedo|Normal|Mask|Roughness|Metallic|AO_|MicroSplat|Splat)'
} | Copy-Item -Destination $dest

# 6. Generate MediaWiki pages
pwsh ../generator/Generate-Pages.ps1

# 7. Push pages to Fandom
pwsh ../uploader/Upload-ToFandom.ps1                             # dry-run
pwsh ../uploader/Upload-ToFandom.ps1 -Commit -DelayMs 3000       # commit

# 8. Push icons to Fandom
pwsh ../uploader/Upload-Assets.ps1 -FromDir ../assets/curated/icons                       # dry-run
pwsh ../uploader/Upload-Assets.ps1 -FromDir ../assets/curated/icons -Commit -DelayMs 3000 # commit
```

## Extractors

| File | Emits | Notes |
|---|---|---|
| `Extractors/BiomeExtractor.cs` | `biomes.json` | 10 biomes from `terrain_*.bundle` filenames + known metadata |
| `Extractors/LocationExtractor.cs` | `locations.json` | 11 POIs from `sh_*.bundle` filenames |
| `Extractors/LootCrateExtractor.cs` | `loot_crates.json` | 6 crate variants from `shc_crates_*.bundle` |
| `Extractors/VehicleBundleExtractor.cs` | `vehicle_bundles.json` | 6 biome vehicle groups from `cars_*.bundle` |
| `Extractors/SkillFactorExtractor.cs` | `skill_factors.json` | 23 `_xxx_Factor` fields on `Char_Skills` |
| `Extractors/TrapExtractor.cs` | `trap_classes.json` | Cecil-derived `Trap_Base` subclasses |
| `Extractors/WeaponExtractor.cs` | `weapon_classes.json` | Cecil-derived `Weapon_Melee` / `Weapon_Range` subclasses |
| `Extractors/BuildCategoryExtractor.cs` | `build_categories.json` | Cecil enum scan for build categories |
| `Extractors/WorkbenchExtractor.cs` | `workbench_types.json` | 11 workbench enum values from `WorkbenchType` |
| `Extractors/SlotTypeExtractor.cs` | `slot_types.json` | 7 `Slot_Type` enum values |
| `Extractors/ManagerExtractor.cs` | `managers.json` | 56 game-authored singleton managers |
| `Extractors/ResourceCatalogExtractor.cs` | `resource_catalog.json` | Cecil scan for `Sound_Mat` and resource-related types |
| `Extractors/CatalogExtractor.cs` | `asset_catalog.json` | Raw asset key catalog by category prefix |
| `Extractors/CatalogEntryParser.cs` | `guid_name_map.json` | **Proper** Unity Addressables binary decoder |
| `Extractors/RecipeExtractor.cs` | `recipe_shapes.json` | Cecil type-shape info for Craft_Items / Build_Info |

## Parse-Recipes.ps1

Reads `wiki/assets/raw/main/**/WB_*.prefab` files (workbench prefabs), extracts `_CraftItemsData → perIconData → matsData`, and resolves every asset GUID via `data/guid_name_map.json`. Emits `data/recipes.json` — 745 recipes across 10 workbench types.

## Uploader scripts

- **Upload-ToFandom.ps1** — for wiki pages (`action=edit`). Diff-aware, retry-with-backoff on rate limit.
- **Upload-Assets.ps1** — for file uploads (`action=upload`, multipart/form-data). Skips existing files by default; `-Overwrite` to force.

Both read credentials from `config.json` (gitignored).

## Data files (checked into git for review)

Under `wiki/data/`:
- All `*.json` outputs from the extractors + Parse-Recipes.ps1
- Regenerable at any time from the game files

## Extracted assets (not in git)

Under `wiki/assets/`:
- `raw/all_icons/` — icon PNGs extracted from icon bundles (~1 GB)
- `raw/main/` — workbench prefabs + textures + mesh assets (~6.9 GB)
- `curated/icons/` — 394 primary icons for upload (~50 MB)
- `*.log` — upload run logs

All gitignored. Delete anytime and regenerate via the setup script above.
