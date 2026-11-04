[CmdletBinding()]
param([switch]$SkipTests, [switch]$NoZip, [switch]$RequireClean)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
$env:DOTNET_CLI_HOME = Join-Path $root '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.tools\nuget'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$localSdk = Join-Path $root '.tools\dotnet\dotnet.exe'
$sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
[xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(-[a-zA-Z0-9.]+)?$') { throw 'Invalid release version.' }
$commit = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Build requires a Git checkout for provenance.' }
$dirty = @(git status --porcelain --untracked-files=normal).Count -gt 0
if ($RequireClean -and $dirty) { throw 'Release requires a clean checkout.' }
$sourceRevision = if ($dirty) { "$commit.dirty" } else { $commit }
$buildProperties = @("-p:SourceRevisionId=$sourceRevision", '-p:ContinuousIntegrationBuild=true')
$runtimeVersion = '156.0.4314.58'
$runtimeHash = '857DCFD6F7035508A29645FA2978C3A6A841F220DF77CC366D318894E19F4792'
$runtimeUrl = 'https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/b7919cc9-680d-4253-bf32-732988e68aa6/Microsoft.WebView2.FixedVersionRuntime.156.0.4314.58.x64.cab'
$runtimeRoot = Join-Path $root '.tools\webview-runtime'
$runtime = Join-Path $runtimeRoot "Microsoft.WebView2.FixedVersionRuntime.$runtimeVersion.x64"
New-Item -ItemType Directory -Force -Path (Join-Path $root '.tools'),(Join-Path $root 'artifacts') | Out-Null
Start-Transcript -LiteralPath (Join-Path $root 'artifacts\build-test.log') -Force | Out-Null
trap { try { Stop-Transcript | Out-Null } catch {}; throw $_ }
if (-not (Test-Path -LiteralPath (Join-Path $runtime 'msedgewebview2.exe'))) {
    $cab = Join-Path $root '.tools\webview.cab'
    if (-not (Test-Path -LiteralPath $cab)) { Invoke-WebRequest -Uri $runtimeUrl -OutFile $cab }
    if ((Get-FileHash -LiteralPath $cab -Algorithm SHA256).Hash -ne $runtimeHash) { throw 'WebView2 package SHA-256 mismatch.' }
    New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null
    & expand.exe $cab '-F:*' $runtimeRoot | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'WebView2 extraction failed.' }
}
& $sdk restore SSHExplorer.sln --locked-mode --disable-parallel | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
& $sdk build SSHExplorer.sln -c Release --no-restore -m:1 -nr:false @buildProperties | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
if (-not $SkipTests) {
    if ($env:CI -ne 'true') { throw 'Window regression checks run only in CI. Use -SkipTests on the host after reviewing CI results.' }
    & $sdk run --project tests\SSHExplorer.Tests\SSHExplorer.Tests.csproj -c Release --no-build | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Risk regression tests failed.' }
    & $sdk run --project tests\SSHExplorer.UiTests\SSHExplorer.UiTests.csproj -c Release --no-build | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Windows virtualization tests failed.' }
    & $sdk run --project tests\SSHExplorer.UiTests\SSHExplorer.UiTests.csproj -c Release --no-build -- --resources-only | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Theme resource tests failed.' }
    foreach ($mode in @('--motion-only','--connection-only','--monitor-graph-only','--monitor-state-only','--monitor-poll-only','--drag-payload-only','--file-state-only','--monitor-refinement-only','--shell-icons-only')) {
        & $sdk run --project tests\SSHExplorer.UiTests\SSHExplorer.UiTests.csproj -c Release --no-build -- $mode | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Headless UI contract checks failed: $mode" }
    }
    foreach ($project in @('SSHExplorer.ProfileTests','SSHExplorer.NativeDrag.Tests')) {
        & $sdk restore "tests\$project\$project.csproj" --locked-mode --disable-parallel | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "$project restore failed." }
        & $sdk build "tests\$project\$project.csproj" -c Release --no-restore -m:1 -nr:false @buildProperties | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "$project build failed." }
        if ($project -eq 'SSHExplorer.NativeDrag.Tests') { & $sdk run --project "tests\$project\$project.csproj" -c Release --no-build -- --abi-only | Out-Host }
        else { & $sdk run --project "tests\$project\$project.csproj" -c Release --no-build | Out-Host }
        if ($LASTEXITCODE -ne 0) { throw "$project failed." }
    }
}
$packageName = "SSHExplorer-$version-win-x64"
$destination = Join-Path $root "artifacts\$packageName"
if (Test-Path -LiteralPath $destination) { throw "Output already exists; choose an unused version or move the previous test output: $destination" }
& $sdk publish src\SSHExplorer.App\SSHExplorer.App.csproj -c Release -r win-x64 --self-contained true --no-restore -m:1 -nr:false @buildProperties -p:PublishTrimmed=false -p:PublishSingleFile=false -o $destination | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
$runtimeDestination = Join-Path $destination 'WebView2Runtime'
New-Item -ItemType Directory -Force -Path $runtimeDestination | Out-Null
Copy-Item -Path (Join-Path $runtime '*') -Destination $runtimeDestination -Recurse -Force
foreach ($document in @('README.md','CHANGELOG.md','ACCEPTANCE.md','REQUIREMENTS.md','ARCHITECTURE.md','THIRD-PARTY-NOTICES.md')) {
    if (Test-Path -LiteralPath $document) { Copy-Item -LiteralPath $document -Destination $destination -Force }
}
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $destination -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $destination -Recurse -Force
[ordered]@{ version=$version; commit=$commit; dirty=$dirty; sdk=(& $sdk --version); runtime='10.0.14'; webView2=$runtimeVersion; utc=[DateTime]::UtcNow.ToString('O') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'build-info.json') -Encoding utf8
$files = Get-ChildItem -LiteralPath $destination -File -Recurse | Where-Object Name -ne 'SHA256SUMS.txt'
$manifest = foreach ($file in $files) { '{0}  {1}' -f (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $file.FullName.Substring($destination.Length + 1).Replace('\','/') }
$manifest | Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS.txt') -Encoding utf8
if($RequireClean){
    & (Join-Path $PSScriptRoot 'Verify-Portable.ps1') -Directory $destination -ExpectedCommit $commit -ExpectedVersion $version -Report (Join-Path $root 'artifacts\portable-verification.json')
}
if (-not $NoZip) {
    $archive = Join-Path $root "artifacts\$packageName.zip"
    Compress-Archive -LiteralPath $destination -DestinationPath $archive -CompressionLevel Optimal -Force
    Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List
    '{0}  {1}' -f (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(),[IO.Path]::GetFileName($archive) | Set-Content -LiteralPath (Join-Path $root 'artifacts\SHA256SUMS.txt') -Encoding utf8
}
Write-Output "Portable application: $destination\SSHExplorer.exe"
Stop-Transcript | Out-Null
