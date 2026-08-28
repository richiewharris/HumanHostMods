<#
.SYNOPSIS
    Uploads binary assets (PNG icons, model previews, etc.) to the Fandom wiki via action=upload.

.DESCRIPTION
    Companion to Upload-ToFandom.ps1 (which handles wiki pages). This one handles the File namespace.

    Uses the same config.json for credentials. Logs in, gets a CSRF token, then POSTs each file
    via multipart/form-data to action=upload.

    Idempotent-ish: by default skips a file if a page File:<name> already exists on the wiki (unless
    -Overwrite is passed). No local diff of image bytes (Fandom rehashes server-side).

.PARAMETER Files
    Local paths to upload. Wildcards accepted. Each file's basename becomes the wiki File name.

.PARAMETER FromDir
    Alternative to -Files: upload every .png in this directory (non-recursive by default).

.PARAMETER Recursive
    Recurse into subdirectories under -FromDir.

.PARAMETER Filter
    File pattern under -FromDir. Default *.png.

.PARAMETER Include
    Optional wildcard filter applied AFTER enumeration; only filenames matching this pattern are uploaded.

.PARAMETER Overwrite
    Replace existing File:<name> pages. Default: skip.

.PARAMETER Commit
    Actually upload. Without this flag: dry-run, prints what would happen.

.PARAMETER DelayMs
    Sleep between uploads. Default 3000ms. File uploads are heavier than edits; be gentle.

.PARAMETER PageText
    Wikitext to attach as the file's description page. Default: minimal category/license note.

.PARAMETER Config
    Path to config.json. Default: sibling of this script.

.EXAMPLE
    ./Upload-Assets.ps1 -FromDir ../assets/curated/icons                        # dry-run
    ./Upload-Assets.ps1 -FromDir ../assets/curated/icons -Commit                # upload all
    ./Upload-Assets.ps1 -FromDir ../assets/curated/icons -Include 'Ore_*' -Commit
    ./Upload-Assets.ps1 -Files ../assets/curated/icons/Iron_Ore.png -Commit
#>
[CmdletBinding()]
param(
    [string[]] $Files,
    [string]   $FromDir,
    [switch]   $Recursive,
    [string]   $Filter = '*.png',
    [string]   $Include,
    [switch]   $Overwrite,
    [switch]   $Commit,
    [int]      $DelayMs = 3000,
    [string]   $PageText = "Extracted from the Human Host game files by [[HHWiki tooling]]. Fair use for wiki reference.`n[[Category:Extracted game asset]]",
    [string]   $Config   = (Join-Path $PSScriptRoot 'config.json')
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Config + file enumeration
# ---------------------------------------------------------------------------
if (-not (Test-Path $Config)) { throw "Missing config file: $Config" }
$cfg = Get-Content $Config -Raw | ConvertFrom-Json

$fileList = @()
if ($Files) {
    foreach ($f in $Files) {
        $fileList += Get-ChildItem $f -File -ErrorAction SilentlyContinue
    }
}
if ($FromDir) {
    $params = @{ Path = $FromDir; File = $true; Filter = $Filter }
    if ($Recursive) { $params.Recurse = $true }
    $fileList += Get-ChildItem @params
}
if ($Include) {
    $fileList = $fileList | Where-Object { $_.Name -like $Include }
}
$fileList = $fileList | Sort-Object FullName -Unique

Write-Host "[assets] api      = $($cfg.wiki_url)"
Write-Host "[assets] bot      = $($cfg.bot_username)"
Write-Host "[assets] mode     = $(if ($Commit) { 'COMMIT' } else { 'dry-run' })"
Write-Host "[assets] files    = $($fileList.Count)"
Write-Host ""

if ($fileList.Count -eq 0) { Write-Host "[assets] nothing to upload."; exit 0 }

# ---------------------------------------------------------------------------
# API helpers (login + CSRF via query string; upload via multipart)
# ---------------------------------------------------------------------------
$script:session = $null
$UA = 'HHWikiAssetUploader/1.0'

function Invoke-MW {
    param([string]$Method, [hashtable]$Body)
    $Body['format'] = 'json'
    $Body['formatversion'] = '2'
    $sessArg = @{}
    if ($script:session) { $sessArg['WebSession'] = $script:session } else { $sessArg['SessionVariable'] = 'newSess' }
    $resp = Invoke-RestMethod -Method $Method -Uri $cfg.wiki_url -Body $Body @sessArg -UserAgent $UA
    if (-not $script:session -and (Get-Variable -Name newSess -Scope Local -ErrorAction Ignore)) { $script:session = $newSess }
    if ($resp.error) { throw "MediaWiki API [$($resp.error.code)]: $($resp.error.info)" }
    return $resp
}

function Login-Fandom {
    $t = Invoke-MW GET @{ action='query'; meta='tokens'; type='login' }
    $r = Invoke-MW POST @{ action='login'; lgname=$cfg.bot_username; lgpassword=$cfg.bot_password; lgtoken=$t.query.tokens.logintoken }
    if ($r.login.result -ne 'Success') { throw "Login failed: $($r.login.result) - $($r.login.reason)" }
    Write-Host "[assets] logged in as $($r.login.lgusername)"
}

function Get-CsrfToken {
    (Invoke-MW GET @{ action='query'; meta='tokens' }).query.tokens.csrftoken
}

function Test-FileExists {
    param([string]$Filename)
    $r = Invoke-MW GET @{ action='query'; titles="File:$Filename" }
    $page = $r.query.pages[0]
    return -not $page.missing
}

function Upload-File {
    param([System.IO.FileInfo]$File, [string]$TargetName, [string]$Csrf)
    # PowerShell 5.1 doesn't have -Form on Invoke-RestMethod, so build multipart manually.
    # Each part is CRLF-delimited per RFC 7578.
    $boundary = [System.Guid]::NewGuid().ToString('N')
    $crlf = "`r`n"
    $enc  = [System.Text.Encoding]::UTF8

    function Field($name, $value) {
        return "--$boundary$crlf" +
               "Content-Disposition: form-data; name=`"$name`"$crlf$crlf" +
               "$value$crlf"
    }

    $textParts = ''
    $textParts += Field 'action'         'upload'
    $textParts += Field 'filename'       $TargetName
    $textParts += Field 'comment'        'Uploaded via HHWiki asset uploader'
    $textParts += Field 'text'           $PageText
    $textParts += Field 'ignorewarnings' '1'
    $textParts += Field 'token'          $Csrf
    $textParts += Field 'format'         'json'
    $textParts += Field 'formatversion'  '2'

    $fileHeader = "--$boundary$crlf" +
                  "Content-Disposition: form-data; name=`"file`"; filename=`"$TargetName`"$crlf" +
                  "Content-Type: application/octet-stream$crlf$crlf"

    $bytes = [System.IO.File]::ReadAllBytes($File.FullName)
    $tail  = "$crlf--$boundary--$crlf"

    $body = New-Object System.IO.MemoryStream
    $textBytes = $enc.GetBytes($textParts + $fileHeader)
    $body.Write($textBytes, 0, $textBytes.Length)
    $body.Write($bytes, 0, $bytes.Length)
    $tailBytes = $enc.GetBytes($tail)
    $body.Write($tailBytes, 0, $tailBytes.Length)
    $bodyBytes = $body.ToArray()

    # Retry on ratelimited
    foreach ($wait in @(0, 30, 60, 90)) {
        if ($wait -gt 0) { Write-Host ("    ratelimited, waiting {0}s..." -f $wait) -ForegroundColor Yellow; Start-Sleep -Seconds $wait }
        try {
            $r = Invoke-RestMethod -Method POST -Uri $cfg.wiki_url -Body $bodyBytes `
                -ContentType "multipart/form-data; boundary=$boundary" `
                -WebSession $script:session -UserAgent $UA
            if ($r.error) {
                if ($r.error.code -eq 'ratelimited') { continue }
                throw "MW upload error [$($r.error.code)]: $($r.error.info)"
            }
            if ($r.upload.result -eq 'Warning' -and $r.upload.warnings.exists) {
                return @{ Result = 'ExistsSkipped'; Filename = $TargetName }
            }
            if ($r.upload.result -ne 'Success') {
                throw "Upload failed: $($r.upload | ConvertTo-Json -Depth 5)"
            }
            return @{ Result = 'Success'; Filename = $r.upload.filename }
        } catch {
            if ($_.Exception.Message -like '*ratelimited*') { continue }
            throw
        }
    }
    throw "Rate-limited after all retries"
}

# ---------------------------------------------------------------------------
# Run
# ---------------------------------------------------------------------------
Login-Fandom
$csrf = if ($Commit) { Get-CsrfToken } else { $null }

$stats = [ordered]@{ uploaded = 0; exists = 0; skipped_dryrun = 0; errors = 0 }
$firstEdit = $true

foreach ($f in $fileList) {
    $target = $f.Name  # keep the local filename as the wiki File name
    Write-Host ("  {0,-45}  ->  File:{1}" -f $f.Name, $target) -NoNewline

    try {
        $exists = Test-FileExists $target
    } catch {
        Write-Host "  [check-error: $_]" -ForegroundColor Red
        $stats.errors++
        continue
    }

    if ($exists -and -not $Overwrite) {
        Write-Host "  [exists, skip]" -ForegroundColor DarkGray
        $stats.exists++
        continue
    }

    if (-not $Commit) {
        Write-Host ("  [DRY-RUN would upload {0} bytes]" -f $f.Length) -ForegroundColor Cyan
        $stats.skipped_dryrun++
        continue
    }

    if (-not $firstEdit) { Start-Sleep -Milliseconds $DelayMs }
    $firstEdit = $false
    try {
        $r = Upload-File -File $f -TargetName $target -Csrf $csrf
        Write-Host ("  [{0}]" -f $r.Result) -ForegroundColor Green
        if ($r.Result -eq 'Success') { $stats.uploaded++ } elseif ($r.Result -eq 'ExistsSkipped') { $stats.exists++ }
    } catch {
        Write-Host "  [upload-error: $_]" -ForegroundColor Red
        $stats.errors++
    }
}

Write-Host ""
Write-Host "[assets] summary:"
$stats.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-16} {1}" -f $_.Key, $_.Value) }
