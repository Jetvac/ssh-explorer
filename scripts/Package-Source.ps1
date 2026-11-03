param([switch]$RequireClean)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
[xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = [string]$properties.Project.PropertyGroup.Version
if ($RequireClean -and @(git status --porcelain --untracked-files=normal).Count) { throw 'Source package requires a clean checkout.' }
New-Item -ItemType Directory -Force -Path (Join-Path $root 'artifacts') | Out-Null
$name = "SSHExplorer-$version-source.zip"
$archive = Join-Path $root "artifacts\$name"
# Only committed source files, never test credentials, caches or local settings.
& git archive --format=zip "--prefix=SSHExplorer-$version-source/" "--output=$archive" HEAD
if ($LASTEXITCODE -ne 0) { throw 'Source archive failed.' }
'{0}  {1}' -f (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(),$name | Add-Content -LiteralPath (Join-Path $root 'artifacts\SHA256SUMS.txt') -Encoding utf8
Get-Item -LiteralPath $archive | Select-Object FullName,Length
