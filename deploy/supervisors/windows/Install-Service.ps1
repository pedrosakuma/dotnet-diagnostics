<#
.SYNOPSIS
    Install dotnet-diagnostics-mcp as a per-user Scheduled Task on Windows.
.DESCRIPTION
    Registers a Scheduled Task that starts at user logon, auto-restarts on failure,
    and runs the dotnet global tool shim from %USERPROFILE%\.dotnet\tools.
    The default local-observer bearer is limited to read-counters.
    Requires the tool to be installed first:
        dotnet tool install -g dotnet-diagnostics-mcp
.PARAMETER Port
    TCP port to bind. Defaults to 8787.
.PARAMETER Token
    Bearer secret. Defaults to a freshly generated 64-character random hex string.
.PARAMETER TokenName
    Auth:BearerTokens entry name. Defaults to local-observer.
.PARAMETER Scopes
    Auth:BearerTokens scope array. Defaults to read-counters.
.PARAMETER Uninstall
    Remove the Scheduled Task, generated launcher, and user-scope authentication
    environment variables.
.EXAMPLE
    .\Install-Service.ps1 -Port 8787
.EXAMPLE
    .\Install-Service.ps1 -Scopes @('read-counters', 'eventpipe')
.EXAMPLE
    .\Install-Service.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [int]$Port = 8787,
    [string]$Token,
    [string]$TokenName = 'local-observer',
    [string[]]$Scopes = @('read-counters'),
    [string]$TaskName = 'dotnet-diagnostics-mcp',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$configurationPrefix = 'Auth__BearerTokens__0__'
$launcherDirectory = Join-Path $env:LOCALAPPDATA 'dotnet-diagnostics-mcp'
$launcherPath = Join-Path $launcherDirectory 'Run-ScheduledTask.ps1'

function Remove-AuthenticationEnvironment {
    foreach ($name in [Environment]::GetEnvironmentVariables('User').Keys) {
        if ($name.ToString().StartsWith($configurationPrefix, [StringComparison]::Ordinal)) {
            [Environment]::SetEnvironmentVariable($name.ToString(), $null, 'User')
        }
    }

    # Remove the value written by releases that used the legacy installer.
    [Environment]::SetEnvironmentVariable('MCP_BEARER_TOKEN', $null, 'User')
}

function Remove-TaskRegistration {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }
}

if ($Uninstall) {
    Remove-TaskRegistration
    Remove-AuthenticationEnvironment
    if (Test-Path $launcherPath) {
        Remove-Item $launcherPath -Force
    }
    if ((Test-Path $launcherDirectory) -and
        -not (Get-ChildItem $launcherDirectory -Force | Select-Object -First 1)) {
        Remove-Item $launcherDirectory -Force
    }

    Write-Host "Removed Scheduled Task '$TaskName' and its user-scope authentication configuration."
    return
}

$exe = Join-Path $env:USERPROFILE '.dotnet\tools\dotnet-diagnostics-mcp.exe'
if (-not (Test-Path $exe)) {
    Write-Error "dotnet-diagnostics-mcp.exe not found at $exe. Install first: dotnet tool install -g dotnet-diagnostics-mcp"
    exit 1
}

if ([string]::IsNullOrWhiteSpace($TokenName)) {
    throw 'TokenName must not be empty.'
}
if ($Scopes.Count -eq 0 -or $Scopes.Where({ -not [string]::IsNullOrWhiteSpace($_) }).Count -ne $Scopes.Count) {
    throw 'Scopes must contain at least one non-empty scope.'
}
if ([string]::IsNullOrWhiteSpace($Token)) {
    $bytes = New-Object byte[] 32
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
    }
    finally {
        $generator.Dispose()
    }
    $Token = ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
}

Remove-AuthenticationEnvironment
[Environment]::SetEnvironmentVariable("${configurationPrefix}Name", $TokenName, 'User')
[Environment]::SetEnvironmentVariable("${configurationPrefix}Token", $Token, 'User')
for ($index = 0; $index -lt $Scopes.Count; $index++) {
    [Environment]::SetEnvironmentVariable(
        "${configurationPrefix}Scopes__$index",
        $Scopes[$index],
        'User')
}

New-Item -ItemType Directory -Path $launcherDirectory -Force | Out-Null
$launcher = @'
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Executable,
    [Parameter(Mandatory = $true)]
    [string]$Urls
)

$ErrorActionPreference = 'Stop'
$prefix = 'Auth__BearerTokens__0__'
$processVariables = [Environment]::GetEnvironmentVariables('Process')
foreach ($name in $processVariables.Keys) {
    if ($name.ToString().StartsWith($prefix, [StringComparison]::Ordinal)) {
        [Environment]::SetEnvironmentVariable($name.ToString(), $null, 'Process')
    }
}
[Environment]::SetEnvironmentVariable('MCP_BEARER_TOKEN', $null, 'Process')

$variables = [Environment]::GetEnvironmentVariables('User')
foreach ($name in $variables.Keys) {
    if ($name.ToString().StartsWith($prefix, [StringComparison]::Ordinal)) {
        [Environment]::SetEnvironmentVariable(
            $name.ToString(),
            $variables[$name].ToString(),
            'Process')
    }
}

& $Executable '--urls' $Urls
exit $LASTEXITCODE
'@
Set-Content -Path $launcherPath -Value $launcher -Encoding UTF8

$powerShellExe = Join-Path $PSHOME 'powershell.exe'
if (-not (Test-Path $powerShellExe)) {
    $powerShellExe = Join-Path $PSHOME 'pwsh.exe'
}
if (-not (Test-Path $powerShellExe)) {
    throw "Unable to locate a PowerShell executable under $PSHOME."
}

$urls = "http://127.0.0.1:$Port"
$arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass " +
    "-File `"$launcherPath`" -Executable `"$exe`" -Urls `"$urls`""
$action = New-ScheduledTaskAction -Execute $powerShellExe -Argument $arguments
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -RestartCount 5 `
    -RestartInterval (New-TimeSpan -Seconds 30) `
    -ExecutionTimeLimit ([System.TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Write-Host "Removing existing task '$TaskName'..."
    Remove-TaskRegistration
}

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal | Out-Null

Write-Host "Registered Scheduled Task '$TaskName' with principal '$TokenName' and scopes: $($Scopes -join ', ')."
Write-Host 'The bearer secret is stored in the current user environment and was not printed.'
Write-Host "Endpoint: $urls/mcp"
Write-Host "Health probe: $exe --health-check --urls $urls"
Write-Host ''
Write-Host "Start now without waiting for logon: Start-ScheduledTask -TaskName '$TaskName'"
Write-Host "Load the client secret without printing it: `$token = [Environment]::GetEnvironmentVariable('${configurationPrefix}Token', 'User')"
Write-Host "Uninstall: .\Install-Service.ps1 -TaskName '$TaskName' -Uninstall"
