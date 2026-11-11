<#
.SYNOPSIS
  Removes build output from the working tree so the next build starts from scratch.
.DESCRIPTION
  Removes: bin\ and obj\ of every .NET project, artifacts\ (installers, publish output),
  _site\ (website build), docs\images\raw\ (screenshot renders), TestResults\, __pycache__\.
  Never touches sources, .git, IDE settings (.idea, .vs) or local config (.claude).
.EXAMPLE
  ./scripts/clean.ps1            # list what will be removed, ask, then delete
  ./scripts/clean.ps1 -Yes       # delete without asking (scripts, CI)
  ./scripts/clean.ps1 -DryRun    # only list what would be deleted
  ./scripts/clean.ps1 -DsStore   # also delete .DS_Store files copied from a Mac
#>
param(
    [switch]$DryRun,
    [switch]$Yes,
    [switch]$DsStore
)
$ErrorActionPreference = "Stop"
$Root = (Resolve-Path "$PSScriptRoot\..").Path
$Git = Join-Path $Root ".git"
function Outside-Git($p) { -not $p.StartsWith($Git, [StringComparison]::OrdinalIgnoreCase) }

$targets = [System.Collections.Generic.List[string]]::new()
# bin\ and obj\ only next to a project file, so unrelated folders named "bin" are left alone
Get-ChildItem $Root -Recurse -Filter *.csproj -File | Where-Object { Outside-Git $_.FullName } | ForEach-Object {
    foreach ($sub in "bin", "obj") {
        $dir = Join-Path $_.DirectoryName $sub
        if (Test-Path $dir) { $targets.Add($dir) }
    }
}
foreach ($path in "artifacts", "_site", "docs\images\raw") {
    $full = Join-Path $Root $path
    if (Test-Path $full) { $targets.Add($full) }
}
Get-ChildItem $Root -Recurse -Directory -Force | Where-Object { $_.Name -in "__pycache__", "TestResults" -and (Outside-Git $_.FullName) } |
    ForEach-Object { $targets.Add($_.FullName) }
if ($DsStore) {
    Get-ChildItem $Root -Recurse -Force -Filter .DS_Store -File | Where-Object { Outside-Git $_.FullName } |
        ForEach-Object { $targets.Add($_.FullName) }
}

if ($targets.Count -eq 0) { Write-Host "Already clean."; return }

Write-Host "Sweepbox repo: $Root"
$totalBytes = 0
foreach ($t in $targets) {
    $bytes = (Get-ChildItem $t -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    if (-not $bytes -and (Test-Path $t -PathType Leaf)) { $bytes = (Get-Item $t -Force).Length }
    $totalBytes += [long]$bytes
    Write-Host ("  {0,8}  {1}" -f ("{0:N0} MB" -f ($bytes / 1MB)), $t.Substring($Root.Length + 1))
}
Write-Host ("Total: about {0:N0} MB." -f ($totalBytes / 1MB))

if ($DryRun) { Write-Host "Dry run: nothing was removed."; return }
# Interactive: confirm first. Non-interactive runs need -Yes.
if (-not $Yes) {
    if ([Console]::IsInputRedirected -or -not [Environment]::UserInteractive) {
        Write-Error "Not interactive: pass -Yes to remove without asking."
    }
    $answer = Read-Host "Remove these? [y/N]"
    if ($answer -notmatch '^[Yy]$') { Write-Host "Nothing removed."; return }
}

foreach ($t in $targets) { if (Test-Path -LiteralPath $t) { Remove-Item -LiteralPath $t -Recurse -Force } }
Write-Host ("Removed. Freed about {0:N0} MB." -f ($totalBytes / 1MB))
