[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedCommit,
    [Alias('Version')][string]$ExpectedVersion = '0.2.0-beta.6'
)
$ErrorActionPreference = 'Stop'

# These guards precede verification, directory creation and, especially, Process.Start.
# This script must never be invoked to open the application on the user's desktop.
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:CI -cne 'true' -or
    [Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or
    $env:RUNNER_OS -ne 'Windows' -or $env:GITHUB_RUN_ID -notmatch '^[1-9][0-9]*$') {
    throw 'Portable startup is permitted only in a Windows GitHub Actions CI run.'
}
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'This CI probe requires PowerShell 7 ArgumentList support.' }
if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+(-[a-zA-Z0-9.]+)?$') { throw 'Invalid expected version.' }
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
if ([string]::IsNullOrWhiteSpace($env:GITHUB_WORKSPACE) -or
    -not [string]::Equals($repositoryRoot, [IO.Path]::GetFullPath($env:GITHUB_WORKSPACE).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) -or
    $env:GITHUB_SHA -ne $ExpectedCommit) {
    throw 'The probe must use the checked-out CI workspace and its exact commit.'
}
$packageRoot = (Resolve-Path -LiteralPath $Directory).ProviderPath.TrimEnd('\')
$expectedPackage = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts\SSHExplorer-$ExpectedVersion-win-x64"))
if (-not [string]::Equals($packageRoot, $expectedPackage, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Directory must be the exact versioned portable folder built in this CI checkout.'
}

$artifactRoot = Join-Path $repositoryRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
$reportPath = Join-Path $artifactRoot 'portable-startup.json'
$safeLogPath = Join-Path $artifactRoot 'portable-startup-diagnostics.jsonl'
$settingsRoot = Join-Path $repositoryRoot ('.tools\portable-startup\' + $env:GITHUB_RUN_ID + '-' + [Guid]::NewGuid().ToString('N'))
$ownedProcess = $null
$ownedProcessId = $null
$stopwatch = [Diagnostics.Stopwatch]::new()
$phase = 'static-verification'
$passed = $false
$records = @()
$report = [ordered]@{
    Status = 'FAIL'; Scope = 'CI packaged EXE normal startup and graceful close only'
    Version = $ExpectedVersion; Commit = $ExpectedCommit; GitHubRunId = $env:GITHUB_RUN_ID
    Windows = [Environment]::OSVersion.VersionString
    StaticVerificationPassed = $false; ApplicationLaunchPerformed = $false
    IsolatedFreshSettings = $false; AppStartedRecord = $false; OwnMainWindowHandleObserved = $false
    GracefulCloseRequested = $false; CloseCompletedRecord = $false; AppExitRecord = $false
    ExitCode = $null; ForcedTermination = $false; ProcessLifetimeMilliseconds = $null
    FailurePhase = $null; FailureType = $null; SafeDiagnostics = 'portable-startup-diagnostics.jsonl'
    PasswordRouteTested = $false; InteractiveUserInputTested = $false; CleanWindowsTested = $false
}

function Read-ProbeRecords([switch]$Strict) {
    $logRoot = Join-Path $settingsRoot 'Logs'
    if (-not (Test-Path -LiteralPath $logRoot -PathType Container)) { return }
    foreach ($file in Get-ChildItem -LiteralPath $logRoot -File | Sort-Object Name) {
        if ($file.Name -notin @('diagnostics.log', 'diagnostics.log.1', 'fatal-emergency.log')) {
            if ($Strict) { throw 'Unexpected diagnostic file in the isolated startup store.' }
            continue
        }
        foreach ($line in Get-Content -LiteralPath $file.FullName) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            try { $entry = $line | ConvertFrom-Json -ErrorAction Stop }
            catch { if ($Strict) { throw 'Invalid completed diagnostic JSON record.' }; continue }
            # While the process is writing, ignore only incomplete records. After exit
            # every record must carry the expected process/build identity.
            if ($null -eq $entry.process -or $null -eq $entry.stage -or $null -eq $entry.build) {
                if ($Strict) { throw 'Diagnostic identity fields are missing.' }
                continue
            }
            $entry
        }
    }
}

function Assert-ProbeRecords([object[]]$Entries) {
    foreach ($entry in $Entries) {
        if ($entry.process -ne $ownedProcessId -or $entry.build -ne "$ExpectedVersion+$ExpectedCommit") {
            throw 'Diagnostic process/build identity does not match the owned packaged EXE.'
        }
        if ($entry.stage -match '(failed|failure|fatal|unhandled)' -or $entry.outcome -match '^(failed|failure|fatal)$' -or
            ($null -ne $entry.failures -and @($entry.failures).Count -gt 0) -or $null -ne $entry.exception) {
            throw 'Packaged startup/close recorded an error.'
        }
    }
}

function Write-SafeProbeDiagnostics([object[]]$Entries) {
    $safe = foreach ($entry in $Entries) {
        # Do not copy settings, environment strings, server responses, messages,
        # stacks or any unrecognized field. This projection is the only log asset.
        if ($entry.process -ne $ownedProcessId -or $entry.build -ne "$ExpectedVersion+$ExpectedCommit" -or
            $entry.stage -notmatch '^[a-zA-Z0-9.-]{1,80}$' -or $entry.outcome -notmatch '^[a-zA-Z0-9.-]{1,80}$') { continue }
        $timestamp = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse([string]$entry.utc, [ref]$timestamp)) { continue }
        [ordered]@{
            utc = $timestamp.ToUniversalTime().ToString('O'); build = [string]$entry.build
            process = [int]$entry.process; stage = [string]$entry.stage; outcome = [string]$entry.outcome
            hasException = (($null -ne $entry.failures -and @($entry.failures).Count -gt 0) -or $null -ne $entry.exception)
        } | ConvertTo-Json -Compress
    }
    [IO.File]::WriteAllLines($safeLogPath, [string[]]@($safe), [Text.UTF8Encoding]::new($false))
}

try {
    # This verifier hashes files and reads version resources. It does not execute PE code.
    & (Join-Path $PSScriptRoot 'Verify-Portable.ps1') -Directory $packageRoot -ExpectedCommit $ExpectedCommit -ExpectedVersion $ExpectedVersion | Out-Null
    $report.StaticVerificationPassed = $true
    $phase = 'fresh-settings'
    if (Test-Path -LiteralPath $settingsRoot) { throw 'The isolated settings directory must be new.' }
    New-Item -ItemType Directory -Path $settingsRoot | Out-Null
    $report.IsolatedFreshSettings = $true
    $phase = 'process-start'
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = Join-Path $packageRoot 'SSHExplorer.exe'
    $startInfo.WorkingDirectory = $packageRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.ArgumentList.Add('--settings-dir')
    $startInfo.ArgumentList.Add($settingsRoot)
    $stopwatch.Start()
    $ownedProcess = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $ownedProcess) { throw 'The packaged process could not be started.' }
    $ownedProcessId = $ownedProcess.Id
    $report.ApplicationLaunchPerformed = $true
    $phase = 'startup'
    # Reserve 15 seconds for graceful close and 5 seconds for owned-PID cleanup.
    while ($stopwatch.Elapsed.TotalSeconds -lt 40) {
        if ($ownedProcess.WaitForExit(150)) { throw 'The packaged process exited before normal startup was observed.' }
        $ownedProcess.Refresh()
        $records = @(Read-ProbeRecords)
        Assert-ProbeRecords $records
        $report.AppStartedRecord = @($records | Where-Object { $_.stage -eq 'app.started' -and $_.process -eq $ownedProcessId }).Count -gt 0
        # MainWindowHandle is obtained from this Process object, never by title/name
        # enumeration, so no other app/window is targeted.
        $report.OwnMainWindowHandleObserved = $ownedProcess.MainWindowHandle -ne [IntPtr]::Zero
        if ($report.AppStartedRecord -and $report.OwnMainWindowHandleObserved) { break }
    }
    if (-not $report.AppStartedRecord -or -not $report.OwnMainWindowHandleObserved) { throw 'Normal packaged startup was not observed within its bounded deadline.' }
    # Startup and memory of the whole owned process tree (WebView2 starts only with a terminal).
    $report.StartupToResponsiveHomeMilliseconds = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds)
    Start-Sleep -Seconds 3
    $ownedProcess.Refresh()
    $tree = @($ownedProcess.Id) + @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($ownedProcess.Id)" | ForEach-Object { [int]$_.ProcessId })
    $workingSet = 0L; $private = 0L
    foreach ($id in $tree) { try { $p = [Diagnostics.Process]::GetProcessById($id); $workingSet += $p.WorkingSet64; $private += $p.PrivateMemorySize64; $p.Dispose() } catch {} }
    $report.ProcessTreeCount = $tree.Count; $report.ProcessTreeWorkingSetBytes = $workingSet; $report.ProcessTreePrivateBytes = $private
    $report.MemoryScope = 'Home tab idle 3 s after startup; no SSH session, terminal or WebView2 instance
    $phase = 'graceful-close'
    $report.GracefulCloseRequested = $ownedProcess.CloseMainWindow()
    if (-not $report.GracefulCloseRequested) { throw 'The owned process did not accept CloseMainWindow.' }
    $remaining = [Math]::Max(1, [int]((55 - $stopwatch.Elapsed.TotalSeconds) * 1000))
    if (-not $ownedProcess.WaitForExit($remaining)) { throw 'The owned process did not exit before its graceful-close deadline.' }
    $report.ExitCode = $ownedProcess.ExitCode
    if ($ownedProcess.ExitCode -ne 0) { throw 'The packaged process exit code was not zero.' }
    $phase = 'exit-diagnostics'
    $records = @(Read-ProbeRecords -Strict)
    Assert-ProbeRecords $records
    $report.CloseCompletedRecord = @($records | Where-Object stage -eq 'app.close.completed').Count -gt 0
    $report.AppExitRecord = @($records | Where-Object { $_.stage -eq 'app.exit' -and $_.outcome -eq '0' }).Count -gt 0
    if (-not $report.CloseCompletedRecord -or -not $report.AppExitRecord) { throw 'Normal-close/app.exit records are missing.' }
    $passed = $true
    $report.Status = 'PASS'
}
catch {
    $report.FailurePhase = $phase
    $report.FailureType = $_.Exception.GetType().FullName
}
finally {
    if ($null -ne $ownedProcess) {
        try {
            if (-not $ownedProcess.HasExited) {
                $passed = $false
                $report.Status = 'FAIL'
                $report.ForcedTermination = $true
                $ownedProcess.Kill() # Only the Process instance created above; never by name or tree.
                $cleanupRemaining = [Math]::Max(1, [Math]::Min(5000, [int]((60 - $stopwatch.Elapsed.TotalSeconds) * 1000)))
                if (-not $ownedProcess.WaitForExit($cleanupRemaining)) { throw 'The owned process did not finish forced cleanup before the probe deadline.' }
            }
            if ($ownedProcess.HasExited) { $report.ExitCode = $ownedProcess.ExitCode }
        }
        catch { $passed = $false; $report.Status = 'FAIL'; $report.FailurePhase = 'owned-process-cleanup'; $report.FailureType = $_.Exception.GetType().FullName }
        $ownedProcess.Dispose()
    }
    if ($stopwatch.IsRunning) { $stopwatch.Stop(); $report.ProcessLifetimeMilliseconds = $stopwatch.ElapsedMilliseconds }
    try { $records = @(Read-ProbeRecords) } catch { $records = @() }
    Write-SafeProbeDiagnostics $records
    $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportPath -Encoding utf8
}
if (-not $passed) { throw 'CI packaged startup probe failed; see artifacts/portable-startup.json and its safe diagnostics projection.' }
Write-Output 'PASS CI packaged EXE normal startup and graceful close; password/live-input/clean-Windows routes were not tested.'
