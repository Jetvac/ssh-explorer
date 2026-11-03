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
$after=Join-Path $root 'artifacts/ui-beta4-after'
$before=Join-Path $root 'artifacts/ui-beta4-before'
New-Item -ItemType Directory -Force -Path $after,$before | Out-Null
& $sdk run --project tests/SSHExplorer.UiTests/SSHExplorer.UiTests.csproj -c Release --no-build -- --monitor-ui-only --render-dir $after 2>&1 | Tee-Object -FilePath artifacts/ui-beta4-after.log
$monitorExit=$LASTEXITCODE
$interactions=Join-Path $root 'artifacts/ui-beta4-interactions'
& $sdk run --project tests/SSHExplorer.UiTests/SSHExplorer.UiTests.csproj -c Release --no-build -- --file-interactions-ui --render-dir $interactions 2>&1 | Tee-Object -FilePath artifacts/ui-beta4-interactions.log
$interactionExit=$LASTEXITCODE
# These scopes run in separate processes. Collect both actual-window reports
# before failing, so a monitor regression cannot hide file/form/tab evidence.
if($monitorExit -or $interactionExit){throw "Current UI checks failed (monitor=$monitorExit; files/forms/tabs=$interactionExit). See both reports; no comparison package was produced."}

# The comparison uses the already-published immutable beta.3 package, not a
# recompiled approximation. The exact same offline fixture and client size are
# laid out by separate harnesses; no desktop capture or image editing is used.
$baselineRoot=Join-Path $root '.tools/beta3-render'
New-Item -ItemType Directory -Force -Path $baselineRoot | Out-Null
$archive=Join-Path $baselineRoot 'SSHExplorer-0.2.0-beta.3-win-x64.zip'
Invoke-WebRequest -Uri 'https://github.com/Jetvac/ssh-explorer/releases/download/v0.2.0-beta.3/SSHExplorer-0.2.0-beta.3-win-x64.zip' -OutFile $archive
$hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if($hash -ne '7a5bfe35cb27b159a8931830764e12af13cb79a344bab58cb06a2fb55992b552'){throw 'Published beta.3 archive checksum mismatch.'}
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $baselineRoot 'portable')
$baseline=Join-Path $baselineRoot 'portable/SSHExplorer-0.2.0-beta.3-win-x64'
& (Join-Path $PSScriptRoot 'Verify-Portable.ps1') -Directory $baseline -ExpectedCommit e8ef6815dd3417dcbda632077ed8408c77576d04 -ExpectedVersion 0.2.0-beta.3 -Report (Join-Path $before 'baseline-portable-verification.json')
$obj=Join-Path $baselineRoot 'obj/'
$lock=Join-Path $baselineRoot 'packages.lock.json'
$bin=Join-Path $baselineRoot 'bin'
& $sdk build tests/SSHExplorer.UiTests/SSHExplorer.UiTests.csproj -c Release -m:1 -nr:false -p:NuGetAudit=false -p:RestoreLockedMode=false "-p:BaselineAssemblyDir=$baseline" "-p:BaseIntermediateOutputPath=$obj" "-p:NuGetLockFilePath=$lock" -o $bin 2>&1 | Tee-Object -FilePath artifacts/ui-beta4-baseline-build.log
if($LASTEXITCODE){throw 'Exact baseline render harness build failed.'}
& (Join-Path $bin 'SSHExplorer.UiTests.exe') --render-beta3baseline --render-dir $before 2>&1 | Tee-Object -FilePath artifacts/ui-beta4-before.log
if($LASTEXITCODE){throw 'Exact baseline composition render failed.'}
$beforeMetadata=Get-Content -LiteralPath (Join-Path $before 'visual-evidence.json') -Raw | ConvertFrom-Json
$afterMetadata=Get-Content -LiteralPath (Join-Path $after 'visual-evidence.json') -Raw | ConvertFrom-Json
if($beforeMetadata.commit -ne 'e8ef6815dd3417dcbda632077ed8408c77576d04' -or $afterMetadata.commit -ne $env:GITHUB_SHA){throw 'Render metadata commit mismatch.'}
$comparisons=foreach($state in @('processes-wide','processes-narrow','performance-gap','files-tree','home')){
    $old=@($beforeMetadata.captures | Where-Object state -eq $state)
    $new=@($afterMetadata.captures | Where-Object state -eq $state)
    if($old.Count -ne 1 -or $new.Count -ne 1){throw "Missing or duplicate comparison state: $state"}
    foreach($dimension in @('width','height')){if([Math]::Abs($old[0].clientDip.$dimension-$new[0].clientDip.$dimension) -gt 0.5){throw "Client geometry differs for $state ($dimension)"}}
    foreach($dimension in @('x','y')){if([Math]::Abs($old[0].dpi.$dimension-$new[0].dpi.$dimension) -gt 0.01){throw "DPI differs for $state"}}
    if($old[0].uiFontSize -ne $new[0].uiFontSize -or $old[0].measurements.theme -ne $new[0].measurements.theme){throw "Font/theme differs for $state"}
    if($old[0].culture -ne $new[0].culture -or $old[0].uiCulture -ne $new[0].uiCulture){throw "Culture differs for $state"}
    [ordered]@{state=$state;clientDip=$new[0].clientDip;dpi=$new[0].dpi;uiFontSize=$new[0].uiFontSize;culture=$new[0].culture;theme=$new[0].measurements.theme}
}
$interactionMetadata=Get-Content -LiteralPath (Join-Path $interactions 'visual-evidence.json') -Raw | ConvertFrom-Json
if($interactionMetadata.commit -ne $env:GITHUB_SHA){throw 'Interaction evidence commit mismatch.'}
$evidenceSets=@([pscustomobject]@{directory=$before;metadata=$beforeMetadata},[pscustomobject]@{directory=$after;metadata=$afterMetadata},[pscustomobject]@{directory=$interactions;metadata=$interactionMetadata})
foreach($set in $evidenceSets){
    foreach($capture in $set.metadata.captures){
        $file=Join-Path $set.directory $capture.file
        if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $capture.sha256){throw "Render hash mismatch: $($capture.state)"}
    }
}
[ordered]@{status='PASS';beforeCommit=$beforeMetadata.commit;afterCommit=$afterMetadata.commit;scope='Matching actual WPF client geometry, DPI, font role and theme; no motion/native caption or physical input claim';states=$comparisons} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $after 'comparison-verification.json') -Encoding utf8
[xml]$properties=Get-Content -LiteralPath Directory.Build.props
$version=[string]$properties.Project.PropertyGroup.Version
$proof=Join-Path $root "artifacts/SSHExplorer-$version-ui-evidence.zip"
Compress-Archive -LiteralPath $after,$before,$interactions -DestinationPath $proof
'{0}  {1}' -f (Get-FileHash -LiteralPath $proof -Algorithm SHA256).Hash.ToLowerInvariant(),[IO.Path]::GetFileName($proof) | Add-Content -LiteralPath artifacts/SHA256SUMS.txt -Encoding utf8
git diff --exit-code -- tests/SSHExplorer.UiTests/packages.lock.json
if($LASTEXITCODE){throw 'Baseline harness changed the normal locked dependency graph.'}
Write-Output "CI WPF client-render evidence: $proof"
