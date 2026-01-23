param(
  [Parameter(Mandatory = $true)]
  [string]$RepoName,

  [ValidateSet('public', 'private')]
  [string]$Visibility = 'public'
)

$ErrorActionPreference = 'Stop'

function Resolve-GhPath {
  $cmd = Get-Command gh -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }

  $fallback = "C:\Program Files\GitHub CLI\gh.exe"
  if (Test-Path $fallback) { return $fallback }

  throw "GitHub CLI (gh) not found. Install it first: winget install --id GitHub.cli -e"
}

$gh = Resolve-GhPath

Write-Host "[INFO] gh: $gh"

& $gh auth status -h github.com | Out-Null
if ($LASTEXITCODE -ne 0) {
  Write-Host "[ERROR] gh is not logged in."
  Write-Host "Run this first, then re-run this script:"
  Write-Host "  & `"$gh`" auth login"
  exit 1
}

$flag = if ($Visibility -eq 'public') { '--public' } else { '--private' }

Write-Host "[INFO] Creating repo and pushing: $RepoName ($Visibility)"
& $gh repo create $RepoName $flag --source . --remote origin --push

Write-Host "[SUCCESS] Done."

