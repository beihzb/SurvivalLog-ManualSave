param(
    [ValidateSet('check', 'backup', 'snapshot', 'watch')]
    [string]$Action = 'check',

    # folder that holds Save_*.bytes / HistorySave.bytes
    [string]$SavesDir = "$env:USERPROFILE\AppData\LocalLow\LLS\SLGame\Saves",

    # where manifests / backups / watch logs are written (kept outside the cloud-synced folder)
    [string]$WorkDir = "$env:TEMP\sl_save_audit",

    # manifest to compare against (defaults to the newest manifest in WorkDir)
    [string]$Baseline = "",

    # watch duration in seconds
    [int]$Seconds = 120
)

# ---------------------------------------------------------------------------
# Save-folder auditor for Survival Log + SLManualSave
#   check    : consistency report (files <-> HistorySave, junk, internal names, duplicates)
#   backup   : zip the whole Save folder before destructive tests
#   snapshot : write a manifest (name/size/mtime/sha256) to compare later
#   watch    : poll the folder and log every write, to see WHICH save gets written
# ---------------------------------------------------------------------------
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $WorkDir)) { New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null }

$savePattern = '^Save_\d{4}(_\d{2}){5}\.bytes$'
$namePattern = 'Save_\d{4}(_\d{2}){5}\.bytes'

function Get-MainSaves {
    Get-ChildItem -LiteralPath $SavesDir -File |
        Where-Object { $_.Name -match $savePattern }
}

function Get-HistoryEntries {
    $path = Join-Path $SavesDir 'HistorySave.bytes'
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $text = [System.Text.Encoding]::GetEncoding(28591).GetString($bytes)
    $names = [regex]::Matches($text, $namePattern) | ForEach-Object { $_.Value }
    # de-duplicate while keeping first-seen order (HistorySave repeats the last-played name once)
    $seen = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($n in $names) { if ($seen.Add($n)) { $n } }
}

function Get-HeaderName([string]$path) {
    try {
        $fs = [System.IO.File]::OpenRead($path)
        try {
            $buffer = New-Object byte[] ([Math]::Min(4096, $fs.Length))
            $read = $fs.Read($buffer, 0, $buffer.Length)
        } finally { $fs.Dispose() }
        $text = [System.Text.Encoding]::GetEncoding(28591).GetString($buffer, 0, $read)
        $m = [regex]::Match($text, $namePattern)
        if ($m.Success) { return $m.Value }
    } catch { }
    return $null
}

function New-Manifest([string]$label) {
    $rows = foreach ($f in (Get-MainSaves | Sort-Object Name)) {
        [pscustomobject]@{
            Name         = $f.Name
            Length       = $f.Length
            LastWriteUtc = $f.LastWriteTimeUtc.ToString('o')
            Sha256       = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.Substring(0, 16)
        }
    }
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $path = Join-Path $WorkDir ("manifest_{0}_{1}.csv" -f $stamp, $label)
    $rows | Export-Csv -LiteralPath $path -NoTypeInformation -Encoding UTF8
    Copy-Item -LiteralPath $path -Destination (Join-Path $WorkDir 'manifest_latest.csv') -Force
    return $path
}

function Write-Section([string]$title) { ""; "== $title ==" }

# ---------------------------------------------------------------------------
if ($Action -eq 'backup') {
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $dest = Join-Path $WorkDir ("saves_backup_{0}.zip" -f $stamp)
    Compress-Archive -Path (Join-Path $SavesDir '*') -DestinationPath $dest -Force
    $size = (Get-Item -LiteralPath $dest).Length / 1MB
    "backup written: $dest ({0:N1} MB)" -f $size
    return
}

if ($Action -eq 'snapshot') {
    $path = New-Manifest 'snapshot'
    "manifest: $path"
    return
}

if ($Action -eq 'watch') {
    $log = Join-Path $WorkDir ("watch_{0}.log" -f (Get-Date -Format 'yyyyMMdd_HHmmss'))
    $state = @{}
    Get-ChildItem -LiteralPath $SavesDir -File | ForEach-Object { $state[$_.Name] = $_.LastWriteTimeUtc }
    "watching for $Seconds s; log: $log"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
        Start-Sleep -Milliseconds 800
        foreach ($f in (Get-ChildItem -LiteralPath $SavesDir -File)) {
            $prev = $state[$f.Name]
            if ($null -eq $prev) {
                $line = "{0:HH:mm:ss.fff}  NEW   {1}  ({2} B)" -f (Get-Date), $f.Name, $f.Length
                $line | Tee-Object -FilePath $log -Append
            } elseif ($prev -ne $f.LastWriteTimeUtc) {
                $line = "{0:HH:mm:ss.fff}  WRITE {1}  ({2} B)" -f (Get-Date), $f.Name, $f.Length
                $line | Tee-Object -FilePath $log -Append
            }
            $state[$f.Name] = $f.LastWriteTimeUtc
        }
    }
    "watch finished; log: $log"
    return
}

# ---------------------------------------------------------------------------
# check
# ---------------------------------------------------------------------------
$mainSaves = @(Get-MainSaves | Sort-Object Name)
$entries = @(Get-HistoryEntries)
$entrySet = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($e in $entries) { [void]$entrySet.Add($e) }
$fileSet = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($f in $mainSaves) { [void]$fileSet.Add($f.Name) }

Write-Section "overview"
"saves folder : $SavesDir"
"main saves   : {0}" -f $mainSaves.Count
"index entries: {0}" -f $entries.Count
"total size   : {0:N1} MB" -f (($mainSaves | Measure-Object Length -Sum).Sum / 1MB)

$fail = 0
$warn = 0

Write-Section "1) index <-> file"
$missingFiles = $entries | Where-Object { -not $fileSet.Contains($_) }
$missingEntries = $mainSaves | Where-Object { -not $entrySet.Contains($_.Name) } | ForEach-Object { $_.Name }
if ($missingFiles.Count -eq 0) { "OK   every index entry has a file" } else { $fail++; "FAIL index entries without file ($($missingFiles.Count)):"; $missingFiles | ForEach-Object { "       $_" } }
if ($missingEntries.Count -eq 0) { "OK   every save file has an index entry" } else { $fail++; "FAIL save files missing from index ($($missingEntries.Count)):"; $missingEntries | ForEach-Object { "       $_" } }

Write-Section "2) junk / leftovers"
$junk = Get-ChildItem -LiteralPath $SavesDir -File | Where-Object { $_.Name -match '\.tmp$|\.corrupt$|\.partial$' -or $_.Length -eq 0 }
if ($junk.Count -eq 0) { "OK   no .tmp / .corrupt / 0-byte files" } else { $warn++; "WARN junk files ($($junk.Count)):"; $junk | ForEach-Object { "       {0} ({1} B)" -f $_.Name, $_.Length } }

Write-Section "3) internal file name"
foreach ($f in $mainSaves) {
    $inside = Get-HeaderName $f.FullName
    if ($inside -eq $f.Name) { "OK   {0}" -f $f.Name }
    elseif ($null -eq $inside) { $warn++; "WARN {0}: no embedded name found in first 4 KB" -f $f.Name }
    else { $fail++; "FAIL {0}: embedded name is '{1}'" -f $f.Name, $inside }
}

Write-Section "4) duplicate content"
$dups = $mainSaves | Group-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } | Where-Object { $_.Count -gt 1 }
if ($dups.Count -eq 0) { "OK   no byte-identical saves" } else { $warn++; "WARN byte-identical saves (expected right after a copy):"; foreach ($g in $dups) { "       " + (($g.Group | ForEach-Object { $_.Name }) -join ', ') } }

Write-Section "5) orphan / side files"
foreach ($f in (Get-ChildItem -LiteralPath $SavesDir -File | Where-Object { $_.Name -like '*.bytes.bak' })) {
    $main = $f.FullName.Substring(0, $f.FullName.Length - 4)
    if (-not (Test-Path -LiteralPath $main)) { $warn++; "WARN orphan .bak: $($f.Name)" }
}
foreach ($f in (Get-ChildItem -LiteralPath $SavesDir -File | Where-Object { $_.Name -match '\.snap\d+\.bytes$' })) {
    $main = Join-Path $SavesDir ($f.Name -replace '\.snap\d+\.bytes$', '.bytes')
    if (-not (Test-Path -LiteralPath $main)) { $warn++; "WARN orphan snapshot: $($f.Name)" }
}
$sideCount = (Get-ChildItem -LiteralPath $SavesDir -File | Where-Object { $_.Name -match '\.snap\d+\.bytes$|\.ending\.\d+$' }).Count
"info side files (snapshots / ending copies): $sideCount"

Write-Section "6) changes vs baseline"
if ([string]::IsNullOrEmpty($Baseline)) {
    $candidate = Join-Path $WorkDir 'manifest_latest.csv'
    if (Test-Path -LiteralPath $candidate) { $Baseline = $candidate }
}
if ([string]::IsNullOrEmpty($Baseline) -or -not (Test-Path -LiteralPath $Baseline)) {
    "info no manifest yet - run:  save_audit.ps1 -Action snapshot"
} else {
    "baseline: $Baseline"
    $old = @{}
    Import-Csv -LiteralPath $Baseline | ForEach-Object { $old[$_.Name] = $_ }
    $current = @{}
    foreach ($f in $mainSaves) {
        $current[$f.Name] = [pscustomobject]@{
            Length       = $f.Length
            LastWriteUtc = $f.LastWriteTimeUtc.ToString('o')
            Sha256       = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.Substring(0, 16)
        }
    }
    foreach ($name in $current.Keys) {
        if (-not $old.ContainsKey($name)) { "NEW    $name" }
        elseif ($old[$name].Sha256 -ne $current[$name].Sha256) { "WROTE  $name" }
    }
    foreach ($name in $old.Keys) { if (-not $current.ContainsKey($name)) { "GONE   $name" } }
    if (-not ($current.Keys | Where-Object { -not $old.ContainsKey($_) })) { "info no new saves since baseline" }
}

Write-Section "summary"
"FAIL={0}  WARN={1}" -f $fail, $warn
if ($fail -eq 0 -and $warn -eq 0) { "RESULT: clean" }
elseif ($fail -eq 0) { "RESULT: usable, warnings above" }
else { "RESULT: problems found - do NOT delete the backup folder" }
