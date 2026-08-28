# Human Host Wiki: Source

Working tree for [human-host.fandom.com](https://human-host.fandom.com/wiki/Human_host_Wiki). Pages are authored here in MediaWiki markup, then copy-pasted into the Fandom editor. Structured data (item lists, perk factors, etc.) is extracted from the shipped game DLLs so pages stay in sync with the game.

**Target game:** Human Host: Virtual Matrix Studio, Early Access since 2026-04-05
**Model wikis:** Valheim, Green Hell, Rust (survival-crafting with progression tiers, biome-gated content, and vehicle/building systems)

## Layout

```
wiki/
├── README.md                # this file
├── pages/                   # MediaWiki source, one file per wiki page
│   ├── Main_Page.mediawiki
│   ├── Getting_Started.mediawiki
│   ├── Controls.mediawiki
│   ├── Combat.mediawiki
│   ├── Crafting.mediawiki           # overview + station list
│   ├── Building.mediawiki           # overview + build category list
│   ├── Biomes.mediawiki             # overview
│   ├── Perks.mediawiki              # overview (Survive / Craft / Fight)
│   ├── Vehicles.mediawiki           # overview
│   ├── Weapons.mediawiki            # overview
│   ├── Items.mediawiki              # overview
│   ├── Food_and_Water.mediawiki
│   ├── Mining.mediawiki
│   ├── Modding.mediawiki            # BepInEx, HHMods suite
│   ├── biomes/                      # one file per biome
│   ├── locations/                   # POIs, house types, loot crates
│   ├── crafting/                    # per-station pages + Recipes hub
│   ├── building/                    # per-build-category pages
│   ├── perks/                       # one file per perk (generated + hand-tweaked)
│   ├── vehicles/                    # parts, Lua, chassis
│   ├── weapons/                     # melee / ranged / ammo
│   ├── items/                       # equipment / resources / food / debris
│   └── mods/                        # HHMods suite docs
├── templates/               # Fandom Template: pages (Infoboxes, Nav, Stub)
├── extractor/               # .NET tool: dumps game DLL metadata to data/*.json
├── generator/               # PowerShell: data/*.json → pages/**/*.mediawiki
└── data/                    # extractor output (checked in for review; regen anytime)
```

## Page-hierarchy design

Modeled on the Valheim wiki's structure (Biomes / Weapons / Food / Armor / Creatures / Crafting / Building), adapted to Human Host's content:

**Top-level hubs**: linked from `Main_Page` and every page's nav template:
1. **[[Getting Started]]**: new-player onboarding
2. **[[Biomes]]**: the eleven procedurally-generated biomes (progression gate)
3. **[[Weapons]]**: Melee, Ranged, Ammunition
4. **[[Items]]**: Equipment, Resources, Food/Water, Debris
5. **[[Crafting]]**: the three workbenches + Recipes hub
6. **[[Building]]**: Foundations, Walls, Functions, Ores, Traps
7. **[[Vehicles]]**: Parts, Lua programming, chassis
8. **[[Perks]]**: Survive / Craft / Fight skill trees
9. **[[Locations]]**: POIs (Airport, Mall, School, Factory Zone, …) and Loot Crates
10. **[[Modding]]**: BepInEx, HHMods

Cross-linking rules:
- Every item page links back to its **workbench** (crafting station) and **biome sources**.
- Every biome page links to its **spawnable POIs**, **native crates**, **native vehicles**, and **native creatures**.
- Every perk page links to the **factor field(s)** on `Char_Skills` it modifies (for players who mod).

## Workflow

### 1. Extract game data
```powershell
cd wiki/extractor
dotnet run -- --game "c:/Program Files (x86)/Steam/steamapps/common/Human Host" --out ../data
```
Produces `data/skills.json`, `data/factors.json`, `data/traps.json`, `data/weapons.json`, `data/build_categories.json`, `data/biomes.json` (derived from asset-bundle names), etc.

### 2. Generate stub pages
```powershell
pwsh wiki/generator/Generate-Pages.ps1
```
Idempotent: existing pages are only overwritten if they were previously generated (frontmatter marker at top of file). Hand-authored pages are left alone.

### 3. Upload to Fandom
For now: copy-paste. A future pass may add an API client using MediaWiki's `action=edit` endpoint with a bot password.

## File naming

- `pages/Foo_Bar.mediawiki` → Fandom page `Foo Bar` (underscores match Fandom URL slugs).
- Subfolders are organizational only: Fandom doesn't have folders. `pages/biomes/Desert.mediawiki` uploads as page `Desert`, not `biomes/Desert`.

## Generated-page marker

Auto-generated pages start with:
```
<!-- HHWIKI:GENERATED source=<script> hash=<content-hash>: remove this line to protect from regeneration -->
```
Delete that line once you edit a page by hand.
