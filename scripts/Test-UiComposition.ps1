[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
if($env:CI -ne 'true' -or $env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_RUN_ID -notmatch '^\d+$'){
    throw 'Actual WPF composition renders require an isolated GitHub Actions Windows runner. Host windows are not authorized.'
}
$root=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
$env:DOTNET_CLI_HOME=Join-Path $root '.tools/dotnet-home'
$env:NUGET_PACKAGES=Join-Path $root '.tools/nuget'
$sdk=if(Test-Path -LiteralPath .tools/dotnet/dotnet.exe){Join-Path $root '.tools/dotnet/dotnet.exe'}else{(Get-Command dotnet -ErrorAction Stop).Source}
$after=Join-Path $root 'artifacts/ui-beta3-after'
$before=Join-Path $root 'artifacts/ui-beta3-before'
New-Item -ItemType Directory -Force -Path $after,$before | Out-Null
& $sdk run --project tests/SSHExplorer.UiTests/SSHExplorer.UiTests.csproj -c Release --no-build -- --monitor-ui-only --render-dir $after 2>&1 | Tee-Object -FilePath artifacts/ui-beta3-after.log
if($LASTEXITCODE){throw 'Current composition layout/render checks failed.'}

# The comparison uses the already-published immutable beta.2 package, not a
# recompiled approximation. The exact same offline fixture and client size are
# laid out by separate harnesses; no desktop capture or image editing is used.
$baselineRoot=Join-Path $root '.tools/beta2-render'
New-Item -ItemType Directory -Force -Path $baselineRoot | Out-Null
$archive=Join-Path $baselineRoot 'SSHExplorer-0.2.0-beta.2-win-x64.zip'
Invoke-WebRequest -Uri 'https://github.com/Jetvac/ssh-explorer/releases/download/v0.2.0-beta.2/SSHExplorer-0.2.0-beta.2-win-x64.zip' -OutFile $archive
$hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if($hash -ne 'c1a77b2ac57225963bc4b4a26de5317258a0b2a737ad1d64e18beb94941846e6'){throw 'Published beta.2 archive checksum mismatch.'}
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $baselineRoot 'portable')
$baseline=Join-Path $baselineRoot 'portable/SSHExplorer-0.2.0-beta.2-win-x64'
& (Join-Path $PSScriptRoot 'Verify-Portable.ps1') -Directory $baseline -ExpectedCommit e8424e548e89b8406444d3122157542597c3c33f -ExpectedVersion 0.2.0-beta.2 -Report (Join-Path $before 'baseline-portable-verification.json')
$obj=Join-Path $baselineRoot 'obj/'
$lock=Join-Path $baselineRoot 'packages.lock.json'
$bin=Join-Path $baselineRoot 'bin'
& $sdk build tests/SSHExplorer.UiTests/SSHExplorer.UiTests.csproj -c Release -m:1 -nr:false -p:NuGetAudit=false -p:RestoreLockedMode=false "-p:BaselineAssemblyDir=$baseline" "-p:BaseIntermediateOutputPath=$obj" "-p:NuGetLockFilePath=$lock" -o $bin 2>&1 | Tee-Object -FilePath artifacts/ui-beta3-baseline-build.log
if($LASTEXITCODE){throw 'Exact baseline render harness build failed.'}
& (Join-Path $bin 'SSHExplorer.UiTests.exe') --render-beta2baseline --render-dir $before 2>&1 | Tee-Object -FilePath artifacts/ui-beta3-before.log
if($LASTEXITCODE){throw 'Exact baseline composition render failed.'}
[xml]$properties=Get-Content -LiteralPath Directory.Build.props
$version=[string]$properties.Project.PropertyGroup.Version
$proof=Join-Path $root "artifacts/SSHExplorer-$version-ui-evidence.zip"
Compress-Archive -LiteralPath $after,$before -DestinationPath $proof
'{0}  {1}' -f (Get-FileHash -LiteralPath $proof -Algorithm SHA256).Hash.ToLowerInvariant(),[IO.Path]::GetFileName($proof) | Add-Content -LiteralPath artifacts/SHA256SUMS.txt -Encoding utf8
git diff --exit-code -- tests/SSHExplorer.UiTests/packages.lock.json
if($LASTEXITCODE){throw 'Baseline harness changed the normal locked dependency graph.'}
Write-Output "CI WPF client-render evidence: $proof"
