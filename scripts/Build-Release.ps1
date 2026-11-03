[CmdletBinding()]
param([switch]$SkipTests, [switch]$NoZip)
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
$runtimeVersion = '156.0.4314.58'
$runtimeHash = '857DCFD6F7035508A29645FA2978C3A6A841F220DF77CC366D318894E19F4792'
$runtimeUrl = 'https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/b7919cc9-680d-4253-bf32-732988e68aa6/Microsoft.WebView2.FixedVersionRuntime.156.0.4314.58.x64.cab'
$runtimeRoot = Join-Path $root '.tools\webview-runtime'
$runtime = Join-Path $runtimeRoot "Microsoft.WebView2.FixedVersionRuntime.$runtimeVersion.x64"
New-Item -ItemType Directory -Force -Path (Join-Path $root '.tools'),(Join-Path $root 'artifacts') | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $runtime 'msedgewebview2.exe'))) {
    $cab = Join-Path $root '.tools\webview.cab'
    if (-not (Test-Path -LiteralPath $cab)) { Invoke-WebRequest -Uri $runtimeUrl -OutFile $cab }
    if ((Get-FileHash -LiteralPath $cab -Algorithm SHA256).Hash -ne $runtimeHash) { throw 'WebView2 package SHA-256 mismatch.' }
    New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null
    & expand.exe $cab '-F:*' $runtimeRoot | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'WebView2 extraction failed.' }
}
& $sdk restore SSHExplorer.sln --locked-mode --disable-parallel
if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
& $sdk build SSHExplorer.sln -c Release --no-restore -m:1 -nr:false
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
if (-not $SkipTests) {
    & $sdk run --project tests\SSHExplorer.Tests\SSHExplorer.Tests.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Risk regression tests failed.' }
    & $sdk run --project tests\SSHExplorer.UiTests\SSHExplorer.UiTests.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Windows virtualization tests failed.' }
}
$destination = Join-Path $root 'artifacts\SSHExplorer-win-x64'
& $sdk publish src\SSHExplorer.App\SSHExplorer.App.csproj -c Release -r win-x64 --self-contained true --no-restore -m:1 -nr:false -p:PublishTrimmed=false -p:PublishSingleFile=false -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
$runtimeDestination = Join-Path $destination 'WebView2Runtime'
New-Item -ItemType Directory -Force -Path $runtimeDestination | Out-Null
Copy-Item -Path (Join-Path $runtime '*') -Destination $runtimeDestination -Recurse -Force
foreach ($document in @('README.md','ACCEPTANCE.md','REQUIREMENTS.md','ARCHITECTURE.md','THIRD-PARTY-NOTICES.md')) {
    if (Test-Path -LiteralPath $document) { Copy-Item -LiteralPath $document -Destination $destination -Force }
}
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $destination -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $destination -Recurse -Force
$files = Get-ChildItem -LiteralPath $destination -File -Recurse | Where-Object Name -ne 'SHA256SUMS.txt'
$manifest = foreach ($file in $files) { '{0}  {1}' -f (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $file.FullName.Substring($destination.Length + 1).Replace('\','/') }
$manifest | Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS.txt') -Encoding utf8
if (-not $NoZip) {
    $archive = Join-Path $root 'artifacts\SSHExplorer-win-x64.zip'
    Compress-Archive -LiteralPath $destination -DestinationPath $archive -CompressionLevel Optimal -Force
    Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List
}
Write-Output "Portable application: $destination\SSHExplorer.exe"
