[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory,
    [string]$Zip,
    [string]$Report
)
# Size breakdown of an unpacked portable package. Categories are assigned by documented path
# rules (see docs/PACKAGING_AND_PERFORMANCE.md); every file falls into exactly one category.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Directory).Path.TrimEnd('\', '/')
$wpfNames = @('Accessibility','DirectWriteForwarder','Microsoft.VisualBasic.Forms','Microsoft.Win32.Registry.AccessControl','Microsoft.Win32.SystemEvents','PresentationCore','PresentationFramework','PresentationFramework.Aero','PresentationFramework.Aero2','PresentationFramework.AeroLite','PresentationFramework.Classic','PresentationFramework.Fluent','PresentationFramework.Luna','PresentationFramework.Royale','PresentationFramework-SystemCore','PresentationFramework-SystemData','PresentationFramework-SystemDrawing','PresentationFramework-SystemXml','PresentationFramework-SystemXmlLinq','PresentationUI','ReachFramework','System.CodeDom','System.Configuration.ConfigurationManager','System.Design','System.Diagnostics.EventLog','System.Diagnostics.EventLog.Messages','System.Diagnostics.PerformanceCounter','System.DirectoryServices','System.Drawing','System.Drawing.Common','System.Drawing.Design','System.Formats.Nrbf','System.IO.Packaging','System.Printing','System.Private.Windows.Core','System.Private.Windows.GdiPlus','System.Resources.Extensions','System.Security.Cryptography.Pkcs','System.Security.Cryptography.Xml','System.Security.Permissions','System.Windows.Controls.Ribbon','System.Windows.Extensions','System.Windows.Forms','System.Windows.Forms.Design','System.Windows.Forms.Design.Editors','System.Windows.Forms.Primitives','System.Windows.Input.Manipulations','System.Windows.Presentation','System.Windows.Primitives','System.Xaml','UIAutomationClient','UIAutomationClientSideProviders','UIAutomationProvider','UIAutomationTypes','WindowsBase','WindowsFormsIntegration','D3DCompiler_47_cor3','PenImc_cor3','PresentationNative_cor3','vcruntime140_cor3','wpfgfx_cor3')
$wpf = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase); $wpfNames | ForEach-Object { [void]$wpf.Add($_) }
$appNames = @('SSHExplorer','SSHExplorer.Core','SSHExplorer.Monitoring')
$thirdParty = @('Renci.SshNet','BouncyCastle.Cryptography','Microsoft.Extensions.Logging.Abstractions','Microsoft.Extensions.DependencyInjection.Abstractions','Microsoft.Web.WebView2.Core','Microsoft.Web.WebView2.Wpf','Microsoft.Web.WebView2.WinForms','WebView2Loader','System.Security.Cryptography.ProtectedData')
function Category([IO.FileInfo]$file) {
    $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
    $top = $relative.Split('/')[0]
    $base = [IO.Path]::GetFileNameWithoutExtension($file.Name)
    if ($top -eq 'WebView2Runtime') { return 'WebView2 Fixed Runtime' }
    if ($top -eq 'Assets') { return 'Terminal assets (xterm.js)' }
    if ($top -eq 'docs' -or ($relative -notmatch '/' -and $file.Extension -eq '.md' -and $file.Name -ne 'THIRD-PARTY-NOTICES.md')) { return 'Documentation' }
    if ($top -eq 'licenses' -or $file.Name -eq 'THIRD-PARTY-NOTICES.md') { return 'Licenses and notices' }
    if ($file.Name -in @('build-info.json','SHA256SUMS.txt')) { return 'Package metadata' }
    if ($appNames -contains $base -or $file.Name -like 'SSHExplorer.*') { return 'SSH Explorer (own code)' }
    if ($thirdParty -contains $base) { return 'Third-party libraries' }
    if ($relative -match '/') { return 'WPF/WinForms satellite resources' }
    if ($wpf.Contains($base)) { return '.NET WindowsDesktop (WPF) runtime' }
    return '.NET runtime (Microsoft.NETCore.App)'
}
$files = Get-ChildItem -LiteralPath $root -File -Recurse
$groups = $files | Group-Object { Category $_ } | ForEach-Object {
    [ordered]@{ category = $_.Name; files = $_.Count; bytes = [long](($_.Group | Measure-Object Length -Sum).Sum) }
} | Sort-Object { -$_.bytes }
$total = [long](($files | Measure-Object Length -Sum).Sum)
$exe = Get-Item -LiteralPath (Join-Path $root 'SSHExplorer.exe') -ErrorAction SilentlyContinue
$result = [ordered]@{
    directory = Split-Path -Leaf $root
    unpackedBytes = $total
    files = $files.Count
    exeBytes = if ($exe) { $exe.Length } else { $null }
    zipBytes = if ($Zip -and (Test-Path -LiteralPath $Zip)) { (Get-Item -LiteralPath $Zip).Length } else { $null }
    categories = @($groups)
    largest = @($files | Sort-Object Length -Descending | Select-Object -First 15 | ForEach-Object { [ordered]@{ path = $_.FullName.Substring($root.Length + 1).Replace('\', '/'); bytes = $_.Length } })
}
$json = $result | ConvertTo-Json -Depth 5
if ($Report) { $json | Set-Content -LiteralPath $Report -Encoding utf8 }
$json
