param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$ExpectedCommit,
    [string]$ExpectedVersion='0.3.1',
    [string]$Report
)
$ErrorActionPreference='Stop'
$packageRoot=(Resolve-Path -LiteralPath $Directory).Path.TrimEnd('\')
$build=Get-Content -LiteralPath (Join-Path $packageRoot 'build-info.json') -Raw | ConvertFrom-Json
if($build.version -ne $ExpectedVersion -or $build.commit -ne $ExpectedCommit -or $build.dirty){throw 'Package provenance mismatch.'}
$required=@('SSHExplorer.exe','SSHExplorer.dll','hostfxr.dll','coreclr.dll','wpfgfx_cor3.dll','WebView2Loader.dll','Assets\Terminal\index.html')
$bundledRuntime=Test-Path -LiteralPath (Join-Path $packageRoot 'WebView2Runtime')
if($build.webView2 -eq 'system-evergreen'){
    if($bundledRuntime){throw 'Evergreen package must not contain WebView2Runtime.'}
} elseif($build.webView2 -like 'fixed-*' -or $build.webView2 -match '^\d+(\.\d+){3}$'){
    # Packages before beta.6 (e.g. the published beta.4 baseline) recorded only the bundled Fixed runtime version.
    $required+='WebView2Runtime\msedgewebview2.exe'
} else {throw "Unknown WebView2 packaging mode: $($build.webView2)"}
foreach($relative in $required){
    if(-not(Test-Path -LiteralPath (Join-Path $packageRoot $relative) -PathType Leaf)){throw "Missing package component: $relative"}
}
$binaryMetadata = foreach($binary in @('SSHExplorer.exe','SSHExplorer.dll','SSHExplorer.Core.dll','SSHExplorer.Monitoring.dll')){
    $binaryPath=Join-Path $packageRoot $binary
    $info=[Diagnostics.FileVersionInfo]::GetVersionInfo($binaryPath)
    if($info.ProductVersion -ne "$ExpectedVersion+$ExpectedCommit"){throw "Binary informational version does not match provenance: $binary"}
    [ordered]@{File=$binary;FileVersion=$info.FileVersion;ProductVersion=$info.ProductVersion;Sha256=(Get-FileHash -LiteralPath $binaryPath -Algorithm SHA256).Hash.ToLowerInvariant()}
}
if(@($binaryMetadata.FileVersion | Select-Object -Unique).Count -ne 1){throw 'Application binaries have inconsistent file versions.'}
$version=$binaryMetadata[1].ProductVersion
$checked=0
$listed=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($line in Get-Content -LiteralPath (Join-Path $packageRoot 'SHA256SUMS.txt')){
    if($line -notmatch '^([0-9a-fA-F]{64})  (.+)$'){throw 'Invalid inner checksum manifest.'}
    $expectedHash=$Matches[1];$relative=$Matches[2]
    $path=[IO.Path]::GetFullPath((Join-Path $packageRoot $relative))
    if(-not $path.StartsWith($packageRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Checksum manifest path escapes package.'}
    if(-not $listed.Add($path)){throw 'Duplicate file in checksum manifest.'}
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expectedHash){throw "Checksum mismatch: $relative"}
    $checked++
}
$actualFiles=@(Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Where-Object FullName -ne (Join-Path $packageRoot 'SHA256SUMS.txt'))
if($actualFiles.Count -ne $listed.Count -or @($actualFiles | Where-Object {-not $listed.Contains($_.FullName)}).Count){throw 'Package contains files not covered by its checksum manifest.'}
$signature=(Get-AuthenticodeSignature -LiteralPath (Join-Path $packageRoot 'SSHExplorer.exe')).Status.ToString()
$result=[ordered]@{Version=$build.version;Commit=$build.commit;Dirty=$build.dirty;AssemblyVersion=$version;Binaries=$binaryMetadata;FilesVerified=$checked;Signature=$signature;Runtime=$build.runtime;WebView2=$build.webView2;StaticVerificationPassed=$true;ApplicationLaunchPerformed=$false}
if($Report){$result | ConvertTo-Json | Set-Content -LiteralPath $Report -Encoding utf8}
$result | ConvertTo-Json
