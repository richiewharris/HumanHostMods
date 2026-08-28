# Fandom uploader

Pushes local `wiki/pages/**/*.mediawiki` and `wiki/templates/*.mediawiki` files to the [human-host.fandom.com](https://human-host.fandom.com/) wiki via the MediaWiki API.

Diff-aware, idempotent, dry-run by default.

## One-time setup

### 1. Create a bot password on the wiki
1. Log in to the Fandom wiki with your normal account.
2. Visit [Special:BotPasswords](https://human-host.fandom.com/wiki/Special:BotPasswords).
3. Create a new bot password. Suggested name: `HHWikiBot`.
4. Grant these permissions:
   - **Basic rights** (read pages)
   - **High-volume editing** (edit pages)
   - **Edit existing pages**
   - **Create, edit, and move pages**
5. Save. The wiki shows you a compound username (`YourUser@HHWikiBot`) and a 32-char password. **This is the only time the password is shown.**

### 2. Fill in credentials
```powershell
Copy-Item wiki/uploader/config.example.json wiki/uploader/config.json
# then edit config.json and paste in the compound username + generated password
```
`config.json` is gitignored - it will not be committed.

### 3. Verify with a dry-run
```powershell
pwsh wiki/uploader/Upload-ToFandom.ps1
```
This logs in, fetches every target page, and reports what would change. Nothing is written.

## Typical workflow

```powershell
# regenerate stubs from latest game data
dotnet run --project wiki/extractor -c Release -- --game 'C:\Program Files (x86)\Steam\steamapps\common\Human Host' --out wiki/data
pwsh wiki/generator/Generate-Pages.ps1

# preview what will change on the live wiki
pwsh wiki/uploader/Upload-ToFandom.ps1

# push (only pages that differ; skips unchanged)
pwsh wiki/uploader/Upload-ToFandom.ps1 -Commit
```

## Common patterns

**Push a single page:**
```powershell
pwsh wiki/uploader/Upload-ToFandom.ps1 -Only 'pages/Main_Page.mediawiki' -Commit
```

**Push only templates (do this first, before any page that references them):**
```powershell
pwsh wiki/uploader/Upload-ToFandom.ps1 -Only 'templates/*' -Commit
```

**Push only biome pages:**
```powershell
pwsh wiki/uploader/Upload-ToFandom.ps1 -Only 'pages/biomes/*' -Commit
```

**Slow down for skittish rate limits:**
```powershell
pwsh wiki/uploader/Upload-ToFandom.ps1 -Commit -DelayMs 2000
```

## How file paths map to page titles

| Local file | Wiki page |
|---|---|
| `wiki/pages/Main_Page.mediawiki` | `Main Page` |
| `wiki/pages/biomes/Mossy_Forest.mediawiki` | `Mossy Forest` |
| `wiki/pages/perks/Adrenaline_(Melee_Damage).mediawiki` | `Adrenaline (Melee Damage)` |
| `wiki/templates/Template_Nav.mediawiki` | `Template:Nav` |
| `wiki/templates/Template_Infobox_biome.mediawiki` | `Template:Infobox biome` |

Rule: strip `.mediawiki`, replace `_` with space. If the filename starts with `Template_`, strip that prefix and prepend `Template:`.

## Diff comparison

Both the local file and the fetched wiki content are normalized before comparison:
- CRLF is converted to LF
- Trailing whitespace on each line is preserved (MediaWiki does not strip it)
- Trailing blank lines are stripped, then exactly one final newline is added

If they match after normalization, the page is skipped and no edit is made. This keeps your revision history clean and avoids "no-op" edits.

## Edit summary

Every commit tags the revision with:
> HHWiki upload from source repo (generated stub) - see wiki/README.md

You can override this by editing the `$summary` line at the bottom of `Upload-ToFandom.ps1`.

## Safety notes

- The uploader does **not** delete pages. If you rename a local file, the old wiki page persists until you delete it manually.
- The uploader does **not** move pages. Rename a wiki page manually if you rename the local file.
- The uploader will overwrite hand-edited wiki content if the local file differs. Use `-Only <path>` to scope pushes, or don't run `-Commit` without checking the dry-run output first.
- Rate limit: default 750ms between edits. Fandom will 429 you if you go faster; if that happens, bump `-DelayMs`.
- The bot password is scoped to the permissions you grant on Special:BotPasswords. If it leaks, revoke it there.
