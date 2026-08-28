<#
.SYNOPSIS
    Uploads locally-authored MediaWiki files to the Human Host Fandom wiki via the MediaWiki API.

.DESCRIPTION
    Idempotent, diff-aware. For each local .mediawiki file:
      1. Compute the target page title from the filename (Template_ prefix -> Template: namespace).
      2. Fetch the current wiki content.
      3. If local == remote (after normalization), skip.
      4. Otherwise, upload via action=edit with a summary tagging the source.

    Dry-run is the default. Pass -Commit to actually write to Fandom.

    Requires a bot password from Special:BotPasswords on the wiki. Store credentials in
    wiki/uploader/config.json (gitignored). See config.example.json for the schema.

.PARAMETER Commit
    Actually push changes to Fandom. Without this flag, the script only reports what would happen.

.PARAMETER Only
    Optional filter: only process files whose relative path (from wiki/) matches this wildcard.
    Examples: "-Only 'pages/biomes/*'", "-Only 'templates/*'", "-Only 'pages/Main_Page.mediawiki'"

.PARAMETER DelayMs
    Milliseconds to sleep between edits. Default 750ms. Fandom throttles aggressive bots.

.PARAMETER Config
    Path to the config file. Default: ./config.json next to this script.

.EXAMPLE
    ./Upload-ToFandom.ps1                              # dry-run everything
    ./Upload-ToFandom.ps1 -Only 'pages/Main_Page.mediawiki' -Commit    # single-page real push
    ./Upload-ToFandom.ps1 -Only 'templates/*' -Commit                  # push all templates
    ./Upload-ToFandom.ps1 -Commit                                      # push everything that differs
#>
[CmdletBinding()]
param(
    [switch] $Commit,
    [string] $Only,
    [int]    $DelayMs = 750,
    [string] $Config  = (Join-Path $PSScriptRoot 'config.json')
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Config
# ---------------------------------------------------------------------------
if (-not (Test-Path $Config)) {
    throw @"
Missing config file: $Config

Copy config.example.json to config.json and fill in:
  - wiki_url:      the api.php URL for your wiki (e.g. https://human-host.fandom.com/api.php)
  - bot_username:  the full BotName@AppName from Special:BotPasswords (e.g. YourUser@HHWikiBot)
  - bot_password:  the generated bot password (32 chars, only shown once when created)

See wiki/uploader/README.md for step-by-step setup.
"@
}

$cfg = Get-Content $Config -Raw | ConvertFrom-Json
foreach ($k in 'wiki_url','bot_username','bot_password') {
    if ([string]::IsNullOrWhiteSpace($cfg.$k)) { throw "Config missing field: $k" }
}

$WikiRoot = Split-Path $PSScriptRoot -Parent  # wiki/
$Api      = $cfg.wiki_url

Write-Host "[uploader] api        = $Api"
Write-Host "[uploader] bot        = $($cfg.bot_username)"
Write-Host "[uploader] mode       = $(if ($Commit) { 'COMMIT' } else { 'dry-run' })"
if ($Only) { Write-Host "[uploader] filter     = $Only" }
Write-Host ""

# ---------------------------------------------------------------------------
# File -> page title mapping
# ---------------------------------------------------------------------------
function Get-PageTitle {
    param([System.IO.FileInfo] $File)
    $rel = $File.FullName.Substring($WikiRoot.Length).TrimStart('\','/')
    $base = [System.IO.Path]::GetFileNameWithoutExtension($File.Name)
    if ($base.StartsWith('Template_')) {
        return 'Template:' + ($base.Substring('Template_'.Length) -replace '_', ' ')
    }
    return $base -replace '_', ' '
}

function Get-RelativePath {
    param([System.IO.FileInfo] $File)
    return ($File.FullName.Substring($WikiRoot.Length).TrimStart('\','/')) -replace '\\','/'
}

# ---------------------------------------------------------------------------
# MediaWiki API helpers
# ---------------------------------------------------------------------------
$session = $null

function Invoke-MW {
    param(
        [Parameter(Mandatory)] [string] $Method,   # 'GET' | 'POST'
        [Parameter(Mandatory)] [hashtable] $Body
    )
    $Body['format'] = 'json'
    $Body['formatversion'] = '2'
    $sessArg = @{}
    if ($script:session) { $sessArg['WebSession'] = $script:session } else { $sessArg['SessionVariable'] = 'newSess' }

    if ($Method -eq 'GET') {
        $resp = Invoke-RestMethod -Method GET -Uri $Api -Body $Body @sessArg -UserAgent 'HHWikiUploader/1.0 (https://github.com/-;)'
    } else {
        $resp = Invoke-RestMethod -Method POST -Uri $Api -Body $Body @sessArg -UserAgent 'HHWikiUploader/1.0 (https://github.com/-;)'
    }
    if (-not $script:session -and (Get-Variable -Name newSess -Scope Local -ErrorAction Ignore)) {
        $script:session = $newSess
    }
    if ($resp.error) {
        throw "MediaWiki API error [$($resp.error.code)]: $($resp.error.info)"
    }
    return $resp
}

function Login-Fandom {
    Write-Host "[uploader] logging in..."
    $t = Invoke-MW -Method GET -Body @{ action='query'; meta='tokens'; type='login' }
    $lgtoken = $t.query.tokens.logintoken
    if (-not $lgtoken) { throw "Failed to obtain login token" }

    $r = Invoke-MW -Method POST -Body @{
        action     = 'login'
        lgname     = $cfg.bot_username
        lgpassword = $cfg.bot_password
        lgtoken    = $lgtoken
    }
    if ($r.login.result -ne 'Success') {
        throw "Login failed: $($r.login.result) - $($r.login.reason)"
    }
    Write-Host "[uploader] logged in as $($r.login.lgusername)"
}

function Get-CsrfToken {
    $r = Invoke-MW -Method GET -Body @{ action='query'; meta='tokens' }
    return $r.query.tokens.csrftoken
}

function Get-RemotePage {
    param([string] $Title)
    $r = Invoke-MW -Method GET -Body @{
        action   = 'query'
        prop     = 'revisions'
        rvprop   = 'content'
        rvslots  = 'main'
        titles   = $Title
    }
    $page = $r.query.pages[0]
    if ($page.missing) { return $null }
    return $page.revisions[0].slots.main.content
}

function Push-Page {
    param([string] $Title, [string] $Content, [string] $Csrf, [string] $Summary)
    # Retry on Fandom's ratelimited response. Backoff: 30s, 60s, 90s.
    $attempts = @(0, 30, 60, 90)
    for ($i = 0; $i -lt $attempts.Length; $i++) {
        if ($attempts[$i] -gt 0) {
            Write-Host ("    ratelimited, waiting {0}s and retrying..." -f $attempts[$i]) -ForegroundColor Yellow
            Start-Sleep -Seconds $attempts[$i]
        }
        try {
            $r = Invoke-MW -Method POST -Body @{
                action  = 'edit'
                title   = $Title
                text    = $Content
                token   = $Csrf
                bot     = '1'
                summary = $Summary
            }
            if ($r.edit.result -ne 'Success') {
                throw "Edit failed for '$Title': $($r.edit | ConvertTo-Json -Depth 5)"
            }
            return $r.edit
        } catch {
            if ($_.Exception.Message -notlike '*ratelimited*') { throw }
            if ($i -eq $attempts.Length - 1) { throw }
        }
    }
}

# ---------------------------------------------------------------------------
# Diff helper
# ---------------------------------------------------------------------------
function Normalize-Content {
    param([string] $Text)
    if ($null -eq $Text) { return '' }
    # Normalize line endings and trim trailing whitespace on each line + trailing newlines overall.
    ($Text -replace "`r`n", "`n" -replace "`r", "`n").TrimEnd() + "`n"
}

# ---------------------------------------------------------------------------
# Enumerate files
# ---------------------------------------------------------------------------
$pageFiles = @()
$pageFiles += Get-ChildItem -Path (Join-Path $WikiRoot 'pages') -Recurse -Filter *.mediawiki -File
$pageFiles += Get-ChildItem -Path (Join-Path $WikiRoot 'templates') -Filter *.mediawiki -File

if ($Only) {
    $pageFiles = $pageFiles | Where-Object {
        $rel = Get-RelativePath $_
        $rel -like $Only
    }
}

Write-Host "[uploader] $($pageFiles.Count) files to consider"
Write-Host ""

# ---------------------------------------------------------------------------
# Log in only if we're actually going to touch the API more than one page.
# The GET (Get-RemotePage) is unauthenticated-safe, but we authenticate up front
# so the CSRF token is valid for the whole session.
# ---------------------------------------------------------------------------
if ($pageFiles.Count -eq 0) {
    Write-Host "[uploader] nothing to do."
    exit 0
}

Login-Fandom
$csrf = if ($Commit) { Get-CsrfToken } else { $null }

# ---------------------------------------------------------------------------
# Process
# ---------------------------------------------------------------------------
$summary = 'HHWiki upload from source repo (generated stub) - see wiki/README.md'
$stats = [ordered]@{ same = 0; diff = 0; new = 0; committed = 0; errors = 0 }
$firstEdit = $true

foreach ($f in $pageFiles) {
    $title = Get-PageTitle $f
    $rel   = Get-RelativePath $f
    $local = Normalize-Content ([System.IO.File]::ReadAllText($f.FullName))

    Write-Host ("  {0,-45}  ->  {1}" -f $rel, $title) -NoNewline
    try {
        $remote = Get-RemotePage $title
    } catch {
        Write-Host "  [ERROR fetching: $_]" -ForegroundColor Red
        $stats.errors++
        continue
    }
    $remoteN = Normalize-Content $remote

    if ($null -eq $remote) {
        Write-Host "  [NEW]" -ForegroundColor Cyan
        $stats.new++
        $shouldEdit = $true
    } elseif ($local -eq $remoteN) {
        Write-Host "  [same]" -ForegroundColor DarkGray
        $stats.same++
        $shouldEdit = $false
    } else {
        $localLen  = ($local  -split "`n").Count
        $remoteLen = ($remoteN -split "`n").Count
        Write-Host ("  [DIFF: local {0}L / remote {1}L]" -f $localLen, $remoteLen) -ForegroundColor Yellow
        $stats.diff++
        $shouldEdit = $true
    }

    if ($shouldEdit -and $Commit) {
        # Space out edits so Fandom's rate limiter stays happy.
        if (-not $firstEdit) { Start-Sleep -Milliseconds $DelayMs }
        $firstEdit = $false

        try {
            $res = Push-Page -Title $title -Content $local -Csrf $csrf -Summary $summary
            Write-Host ("    committed: newrev {0}" -f $res.newrevid) -ForegroundColor Green
            $stats.committed++
        } catch {
            Write-Host "    [COMMIT ERROR: $_]" -ForegroundColor Red
            $stats.errors++
        }
    }
}

Write-Host ""
Write-Host "[uploader] summary:"
$stats.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-10} {1}" -f $_.Key, $_.Value) }

if (-not $Commit -and ($stats.diff + $stats.new) -gt 0) {
    Write-Host ""
    Write-Host "[uploader] this was a dry-run. Re-run with -Commit to push changes." -ForegroundColor Yellow
}
