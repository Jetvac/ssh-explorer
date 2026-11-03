#requires -Version 7.2
[CmdletBinding()]
param(
    [string]$Python = 'python',
    [string]$Dotnet = '',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$RunOrdinaryUi,
    [string]$EvidenceDirectory = 'artifacts/auth-network'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root

# Check this before building or starting any process. CI environment variables are
# required in addition to the explicit switch; a normal host run is headless only.
if ($RunOrdinaryUi -and ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows' -or
    $env:GITHUB_RUN_ID -notmatch '^\d+$' -or $env:CI -ne 'true')) {
    throw 'Ordinary WPF dialog checks are restricted to Windows GitHub Actions. Omit -RunOrdinaryUi on the host.'
}
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'This test driver requires Windows.' }
$pythonExe = (Get-Command $Python -ErrorAction Stop).Source
if (-not $Dotnet) {
    $localSdk = Join-Path $root '.tools/dotnet/dotnet.exe'
    $Dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
$dotnetExe = (Get-Command $Dotnet -ErrorAction Stop).Source
$evidence = [IO.Path]::GetFullPath((Join-Path $root $EvidenceDirectory))
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$run = [Guid]::NewGuid().ToString('N')
$work = Join-Path $root ".tools/auth-network-driver/$run"
$smokeOut = Join-Path $work 'smoke'
$profileOut = Join-Path $work 'profiles'
$fixtureRoot = Join-Path $work 'fixture'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $root '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.tools/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Start-OwnedProcess([string]$Executable, [string[]]$Arguments, [string]$LogName) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $root
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $outPath = Join-Path $evidence "$LogName.stdout.log"
    $errPath = Join-Path $evidence "$LogName.stderr.log"
    $outFile = [IO.FileStream]::new($outPath,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    $errFile = [IO.FileStream]::new($errPath,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try {
        $process = [Diagnostics.Process]::Start($start)
        return [pscustomobject]@{ Process=$process; OutFile=$outFile; ErrFile=$errFile; OutPath=$outPath; ErrPath=$errPath;
            OutCopy=$process.StandardOutput.BaseStream.CopyToAsync($outFile); ErrCopy=$process.StandardError.BaseStream.CopyToAsync($errFile) }
    }
    catch { $outFile.Dispose(); $errFile.Dispose(); throw }
}

function Stop-OwnedProcess($Handle) {
    if (-not $Handle) { return }
    try {
        if (-not $Handle.Process.HasExited) { $Handle.Process.Kill($true) }
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        while (-not $Handle.Process.HasExited -and $deadline.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 100 }
        if (-not $Handle.Process.HasExited) { throw 'Owned test process did not terminate within ten seconds.' }
        $null = $Handle.OutCopy.GetAwaiter().GetResult()
        $null = $Handle.ErrCopy.GetAwaiter().GetResult()
    }
    finally { $Handle.OutFile.Dispose(); $Handle.ErrFile.Dispose(); $Handle.Process.Dispose() }
}

function Invoke-OwnedProcess([string]$Executable, [string[]]$Arguments, [string]$LogName,
    [int]$TimeoutSeconds = 180, [string[]]$Redactions = @()) {
    Write-Host "Starting test stage: $LogName"
    $handle = Start-OwnedProcess $Executable $Arguments $LogName
    try {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (-not $handle.Process.HasExited) {
            if ($timer.Elapsed.TotalSeconds -gt $TimeoutSeconds) { throw "Test stage $LogName exceeded $TimeoutSeconds seconds." }
            Start-Sleep -Milliseconds 100
        }
        $exitCode = $handle.Process.ExitCode
        $null = $handle.OutCopy.GetAwaiter().GetResult(); $null = $handle.ErrCopy.GetAwaiter().GetResult()
    }
    finally { Stop-OwnedProcess $handle }
    foreach ($path in @($handle.OutPath,$handle.ErrPath)) {
        if ($Redactions.Count -gt 0) {
            $text = [IO.File]::ReadAllText($path)
            foreach ($secret in $Redactions) { if ($secret) { $text = $text.Replace($secret,'[redacted-test-secret]') } }
            [IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($false))
        }
        Get-Content -LiteralPath $path -Tail 120 | Write-Host
    }
    if ($exitCode -ne 0) { throw "Test stage $LogName failed with exit code $exitCode. See its test logs." }
}

$fixture = $null
$complete = $false
try {
    # Only known fixture dependencies are reported; do not dump a developer's package list.
    $dependencyCheck = @'
import importlib.metadata as m,json,platform
expected={'paramiko':'4.0.0','bcrypt':'5.0.0','cryptography':'50.0.2','invoke':'3.0.3','PyNaCl':'1.6.2','cffi':'2.1.1','pycparser':'3.11','typing_extensions':'4.16.0'}
actual={name:m.version(name) for name in expected}
assert actual==expected,'Install the pinned test-only requirements-auth.txt into the selected Python environment'
import paramiko,bcrypt,cryptography,cffi,nacl
print(json.dumps({'python':platform.python_version(),'machine':platform.machine(),'dependencies':actual}))
'@
    Invoke-OwnedProcess $pythonExe @('-c',$dependencyCheck) 'python-dependencies' 30
    if ($env:GITHUB_ACTIONS -eq 'true') {
        $dependencyMetadata = Get-Content -LiteralPath (Join-Path $evidence 'python-dependencies.stdout.log') -Raw | ConvertFrom-Json
        if ($dependencyMetadata.python -notmatch '^3\.13\.') { throw 'The pinned Windows CI wheel set requires CPython 3.13.' }
    }
    Invoke-OwnedProcess $dotnetExe @('restore','tests/SSHExplorer.Core.Smoke/SSHExplorer.Core.Smoke.csproj','--locked-mode','--disable-parallel') 'smoke-restore'
    Invoke-OwnedProcess $dotnetExe @('build','tests/SSHExplorer.Core.Smoke/SSHExplorer.Core.Smoke.csproj','-c',$Configuration,'--no-restore','-m:1','-nr:false',"-p:OutDir=$smokeOut/") 'smoke-build'
    Invoke-OwnedProcess $dotnetExe @((Join-Path $smokeOut 'SSHExplorer.Core.Smoke.dll'),'--auth-unit') 'auth-component' 30
    Invoke-OwnedProcess $dotnetExe @((Join-Path $smokeOut 'SSHExplorer.Core.Smoke.dll'),'--auth-scripted',$pythonExe) 'auth-scripted' 120
    if ($RunOrdinaryUi) {
        Invoke-OwnedProcess $dotnetExe @('restore','tests/SSHExplorer.ProfileTests/SSHExplorer.ProfileTests.csproj','--locked-mode','--disable-parallel') 'profile-restore'
        Invoke-OwnedProcess $dotnetExe @('build','tests/SSHExplorer.ProfileTests/SSHExplorer.ProfileTests.csproj','-c',$Configuration,'--no-restore','-m:1','-nr:false',"-p:OutDir=$profileOut/") 'profile-build'
        $fixture = Start-OwnedProcess $pythonExe @((Join-Path $root 'scripts/testing/auth_fixture.py'),$fixtureRoot) 'ordinary-fixture'
        $configurationFile = Join-Path $fixtureRoot 'fixture.json'
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $configurationFile)) {
            if ($fixture.Process.HasExited) { throw 'Ordinary SSH fixture exited before becoming ready.' }
            if ($timer.Elapsed.TotalSeconds -gt 20) { throw 'Ordinary SSH fixture readiness timed out.' }
            Start-Sleep -Milliseconds 100
        }
        $configuration = Get-Content -LiteralPath $configurationFile -Raw | ConvertFrom-Json
        if ($configuration.Host -ne '127.0.0.1' -or $configuration.ExpectedHostFingerprint -notmatch '^SHA256:[A-Za-z0-9+/]{43}$') {
            throw 'Ordinary fixture must be generated loopback with an independently recorded host fingerprint.'
        }
        $redactions = @([IO.File]::ReadAllText($configuration.SecretFile),[IO.File]::ReadAllText($configuration.OtpFile))
        # Launch its apphost: the fresh-process restart scenario uses Environment.ProcessPath.
        Invoke-OwnedProcess (Join-Path $profileOut 'SSHExplorer.ProfileTests.exe') @('--ordinary-only','--ordinary-fixture',$configurationFile) 'ordinary-sta-network' 120 $redactions
    }
    $complete = $true
}
finally {
    Stop-OwnedProcess $fixture
    [ordered]@{ utc=[DateTime]::UtcNow.ToString('O'); commit=(git rev-parse HEAD).Trim(); completed=$complete;
        guiMode=if($RunOrdinaryUi){'CI STA component + real network + fresh test process'}else{'NOT RUN; headless only'};
        liveKeyboard='NOT RUN'; publishedExe='NOT RUN'; fixtureSecrets='ignored .tools only; not uploaded';
        requirementsSha256=(Get-FileHash -LiteralPath (Join-Path $root 'scripts/testing/requirements-auth.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'scope.json') -Encoding utf8
}
Write-Output "Auth test evidence: $evidence"
