$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$archivePath = Join-Path $root 'artifacts\SSHExplorer-source.zip'
New-Item -ItemType Directory -Force -Path (Join-Path $root 'artifacts') | Out-Null
$rootFiles = Get-ChildItem -LiteralPath $root -File -Force | Where-Object { $_.Name -notmatch '\.user$' }
$projectFiles = foreach ($directory in @('src','tests','docs','scripts','licenses')) {
    Get-ChildItem -LiteralPath (Join-Path $root $directory) -File -Recurse | Where-Object { $_.FullName -notmatch '\\(bin|obj|\.vs)\\' -and $_.Name -notmatch '\.user$' }
}
$stream = [IO.File]::Open($archivePath,[IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in @($rootFiles) + @($projectFiles) | Sort-Object FullName) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,('SSHExplorer-source/' + $relative),[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $archive.Dispose(); $stream.Dispose() }
Get-Item -LiteralPath $archivePath | Select-Object FullName,Length
Get-FileHash -LiteralPath $archivePath -Algorithm SHA256 | Format-List
