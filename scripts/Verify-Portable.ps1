param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$ExpectedCommit,
    [string]$ExpectedVersion='0.2.0-beta.2',
    [string]$Report
)
$ErrorActionPreference='Stop'
$packageRoot=(Resolve-Path -LiteralPath $Directory).Path.TrimEnd('\')
$build=Get-Content -LiteralPath (Join-Path $packageRoot 'build-info.json') -Raw | ConvertFrom-Json
if($build.version -ne $ExpectedVersion -or $build.commit -ne $ExpectedCommit -or $build.dirty){throw 'Package provenance mismatch.'}
foreach($relative in @('SSHExplorer.exe','SSHExplorer.dll','hostfxr.dll','coreclr.dll','wpfgfx_cor3.dll','WebView2Runtime\msedgewebview2.exe','Assets\Terminal\index.html')){
    if(-not(Test-Path -LiteralPath (Join-Path $packageRoot $relative) -PathType Leaf)){throw "Missing package component: $relative"}
}
$version=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $packageRoot 'SSHExplorer.dll')).ProductVersion
if(-not $version.StartsWith($ExpectedVersion) -or -not $version.Contains($ExpectedCommit)){throw 'Assembly informational version does not match provenance.'}
$checked=0
foreach($line in Get-Content -LiteralPath (Join-Path $packageRoot 'SHA256SUMS.txt')){
    if($line -notmatch '^([0-9a-fA-F]{64})  (.+)$'){throw 'Invalid inner checksum manifest.'}
    $expectedHash=$Matches[1];$relative=$Matches[2]
    $path=[IO.Path]::GetFullPath((Join-Path $packageRoot $relative))
    if(-not $path.StartsWith($packageRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Checksum manifest path escapes package.'}
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expectedHash){throw "Checksum mismatch: $relative"}
    $checked++
}
$signature=(Get-AuthenticodeSignature -LiteralPath (Join-Path $packageRoot 'SSHExplorer.exe')).Status.ToString()
$result=[ordered]@{Version=$build.version;Commit=$build.commit;Dirty=$build.dirty;AssemblyVersion=$version;FilesVerified=$checked;Signature=$signature;Runtime=$build.runtime;WebView2=$build.webView2;StaticVerificationPassed=$true;ApplicationLaunchPerformed=$false}
if($Report){$result | ConvertTo-Json | Set-Content -LiteralPath $Report -Encoding utf8}
$result | ConvertTo-Json
