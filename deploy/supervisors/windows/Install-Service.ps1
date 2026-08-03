<#
.SYNOPSIS
    Install or update dotnet-diagnostics-mcp as a per-user Scheduled Task.
.DESCRIPTION
    Registers a Scheduled Task that starts at user logon, auto-restarts on failure,
    and runs the dotnet global tool shim from %USERPROFILE%\.dotnet\tools.
    The default local-observer bearer is limited to read-counters.
    Non-secret installation settings are retained so rotation and scope expansion
    do not reset the port, task name, principal name, or existing scopes.
    Requires the tool to be installed first:
        dotnet tool install -g dotnet-diagnostics-mcp
.PARAMETER Port
    TCP port to bind. Defaults to 8787 on first install.
.PARAMETER Token
    Bearer secret. Defaults to a freshly generated 64-character random hex string
    on first install. Use with a normal install or -RotateToken for an explicit value.
.PARAMETER TokenName
    Auth:BearerTokens entry name. Defaults to local-observer on first install.
.PARAMETER Scopes
    Complete Auth:BearerTokens scope array. Defaults to read-counters on first install.
.PARAMETER AddScopes
    Add scopes to an existing installation without changing its token or other settings.
.PARAMETER RotateToken
    Replace only the token in an existing installation.
.PARAMETER Uninstall
    Remove the Scheduled Task, generated launcher, retained state, and user-scope
    authentication environment variables.
.EXAMPLE
    .\Install-Service.ps1 -Port 8787
.EXAMPLE
    .\Install-Service.ps1 -AddScopes @('eventpipe', 'investigation-export')
.EXAMPLE
    .\Install-Service.ps1 -RotateToken
.EXAMPLE
    .\Install-Service.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [int]$Port,
    [string]$Token,
    [string]$TokenName,
    [string[]]$Scopes,
    [string[]]$AddScopes,
    [string]$TaskName,
    [switch]$RotateToken,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$configurationPrefix = 'Auth__BearerTokens__0__'
$launcherDirectory = Join-Path $env:LOCALAPPDATA 'dotnet-diagnostics-mcp'
$launcherPath = Join-Path $launcherDirectory 'Run-ScheduledTask.ps1'
$statePath = Join-Path $launcherDirectory 'install-state.json'
$state = if (Test-Path $statePath) {
    Get-Content $statePath -Raw | ConvertFrom-Json
}
else {
    $null
}

function New-RandomToken {
    $bytes = New-Object byte[] 32
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
    }
    finally {
        $generator.Dispose()
    }

    return ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
}

function Remove-AuthenticationEnvironment {
    foreach ($name in [Environment]::GetEnvironmentVariables('User').Keys) {
        if ($name.ToString().StartsWith($configurationPrefix, [StringComparison]::Ordinal)) {
            [Environment]::SetEnvironmentVariable($name.ToString(), $null, 'User')
        }
    }

    # Remove the value written by releases that used the legacy installer.
    [Environment]::SetEnvironmentVariable('MCP_BEARER_TOKEN', $null, 'User')
}

function Remove-TaskRegistration([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) {
        return
    }

    if (Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $Name -Confirm:$false
    }
}

$hasPort = $PSBoundParameters.ContainsKey('Port')
$hasToken = $PSBoundParameters.ContainsKey('Token')
$hasTokenName = $PSBoundParameters.ContainsKey('TokenName')
$hasScopes = $PSBoundParameters.ContainsKey('Scopes')
$hasAddScopes = $PSBoundParameters.ContainsKey('AddScopes')
$hasTaskName = $PSBoundParameters.ContainsKey('TaskName')

if ($Uninstall -and ($RotateToken -or $hasPort -or $hasToken -or $hasTokenName -or $hasScopes -or $hasAddScopes)) {
    throw '-Uninstall may be combined only with -TaskName.'
}
if ($RotateToken -and ($hasPort -or $hasTokenName -or $hasScopes -or $hasAddScopes -or $hasTaskName)) {
    throw '-RotateToken changes only the token; omit port, task name, token name, and scope parameters.'
}
if ($hasAddScopes -and ($hasPort -or $hasToken -or $hasTokenName -or $hasScopes -or $hasTaskName)) {
    throw '-AddScopes preserves the installed token and settings; do not combine it with other configuration parameters.'
}
if ($hasAddScopes -and $AddScopes.Count -eq 0) {
    throw '-AddScopes requires at least one scope.'
}

$installedTaskName = if ($null -ne $state -and
    -not [string]::IsNullOrWhiteSpace([string]$state.TaskName)) {
    [string]$state.TaskName
}
else {
    $null
}
$resolvedTaskName = if ($hasTaskName) {
    $TaskName
}
elseif ($null -ne $installedTaskName) {
    $installedTaskName
}
else {
    'dotnet-diagnostics-mcp'
}

if ($Uninstall) {
    Remove-TaskRegistration $resolvedTaskName
    if ($null -ne $installedTaskName -and
        -not [string]::Equals($installedTaskName, $resolvedTaskName, [StringComparison]::Ordinal)) {
        Remove-TaskRegistration $installedTaskName
    }

    Remove-AuthenticationEnvironment
    if (Test-Path $launcherPath) {
        Remove-Item $launcherPath -Force
    }
    if (Test-Path $statePath) {
        Remove-Item $statePath -Force
    }
    if ((Test-Path $launcherDirectory) -and
        -not (Get-ChildItem $launcherDirectory -Force | Select-Object -First 1)) {
        Remove-Item $launcherDirectory -Force
    }

    Write-Host "Removed Scheduled Task '$resolvedTaskName' and its user-scope authentication configuration."
    return
}

if (($RotateToken -or $hasAddScopes) -and $null -eq $state) {
    throw 'No retained installation state was found. Run the installer normally once before using update modes.'
}

$resolvedPort = if ($hasPort) {
    $Port
}
elseif ($null -ne $state -and $null -ne $state.Port) {
    [int]$state.Port
}
else {
    8787
}
$resolvedTokenName = if ($hasTokenName) {
    $TokenName
}
elseif ($null -ne $state -and
    -not [string]::IsNullOrWhiteSpace([string]$state.TokenName)) {
    [string]$state.TokenName
}
elseif (-not [string]::IsNullOrWhiteSpace(
    [Environment]::GetEnvironmentVariable("${configurationPrefix}Name", 'User'))) {
    [Environment]::GetEnvironmentVariable("${configurationPrefix}Name", 'User')
}
else {
    'local-observer'
}

$installedScopes = @()
for ($scopeIndex = 0; ; $scopeIndex++) {
    $installedScope = [Environment]::GetEnvironmentVariable(
        "${configurationPrefix}Scopes__$scopeIndex",
        'User')
    if ([string]::IsNullOrWhiteSpace($installedScope)) {
        break
    }

    $installedScopes += $installedScope
}
$scopeCandidates = if ($hasScopes) {
    @($Scopes)
}
elseif ($null -ne $state -and $null -ne $state.Scopes) {
    @($state.Scopes)
}
elseif ($installedScopes.Count -gt 0) {
    $installedScopes
}
else {
    @('read-counters')
}
if ($hasAddScopes) {
    $scopeCandidates += @($AddScopes)
}
$resolvedScopes = @()
foreach ($scope in $scopeCandidates) {
    if ([string]::IsNullOrWhiteSpace($scope)) {
        throw 'Scopes must contain only non-empty values.'
    }

    $normalized = $scope.Trim()
    if ($resolvedScopes -notcontains $normalized) {
        $resolvedScopes += $normalized
    }
}

$existingToken = [Environment]::GetEnvironmentVariable(
    "${configurationPrefix}Token",
    'User')
$resolvedToken = if ($RotateToken) {
    if ($hasToken) { $Token } else { New-RandomToken }
}
elseif ($hasToken) {
    $Token
}
elseif (-not [string]::IsNullOrWhiteSpace($existingToken)) {
    $existingToken
}
elseif ($hasAddScopes) {
    throw 'The installed token is missing; scope expansion stopped rather than rotating it implicitly.'
}
else {
    New-RandomToken
}

if ($resolvedPort -lt 1 -or $resolvedPort -gt 65535) {
    throw 'Port must be between 1 and 65535.'
}
if ([string]::IsNullOrWhiteSpace($resolvedTaskName)) {
    throw 'TaskName must not be empty.'
}
if ([string]::IsNullOrWhiteSpace($resolvedTokenName)) {
    throw 'TokenName must not be empty.'
}
if ([string]::IsNullOrWhiteSpace($resolvedToken)) {
    throw 'Token must not be empty.'
}
if ($resolvedScopes.Count -eq 0) {
    throw 'Scopes must contain at least one scope.'
}

$exe = Join-Path $env:USERPROFILE '.dotnet\tools\dotnet-diagnostics-mcp.exe'
if (-not (Test-Path $exe)) {
    Write-Error "dotnet-diagnostics-mcp.exe not found at $exe. Install first: dotnet tool install -g dotnet-diagnostics-mcp"
    exit 1
}

Remove-AuthenticationEnvironment
[Environment]::SetEnvironmentVariable("${configurationPrefix}Name", $resolvedTokenName, 'User')
[Environment]::SetEnvironmentVariable("${configurationPrefix}Token", $resolvedToken, 'User')
for ($index = 0; $index -lt $resolvedScopes.Count; $index++) {
    [Environment]::SetEnvironmentVariable(
        "${configurationPrefix}Scopes__$index",
        $resolvedScopes[$index],
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

$urls = "http://127.0.0.1:$resolvedPort"
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

if ($null -ne $installedTaskName -and
    -not [string]::Equals($installedTaskName, $resolvedTaskName, [StringComparison]::Ordinal)) {
    Remove-TaskRegistration $installedTaskName
}
if (Get-ScheduledTask -TaskName $resolvedTaskName -ErrorAction SilentlyContinue) {
    Write-Host "Replacing existing task '$resolvedTaskName'..."
    Remove-TaskRegistration $resolvedTaskName
}

Register-ScheduledTask -TaskName $resolvedTaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal | Out-Null

[pscustomobject]@{
    Version = 1
    TaskName = $resolvedTaskName
    Port = $resolvedPort
    TokenName = $resolvedTokenName
    Scopes = @($resolvedScopes)
} | ConvertTo-Json -Depth 3 | Set-Content -Path $statePath -Encoding UTF8

$operation = if ($RotateToken) {
    'Rotated the bearer for'
}
elseif ($hasAddScopes) {
    'Expanded scopes for'
}
else {
    'Installed or updated'
}
Write-Host "$operation Scheduled Task '$resolvedTaskName' with principal '$resolvedTokenName' and scopes: $($resolvedScopes -join ', ')."
Write-Host 'The bearer secret is stored in the current user environment and was not printed.'
Write-Host 'Non-secret port, task, principal, and scope settings were retained for update modes.'
Write-Host "Endpoint: $urls/mcp"
Write-Host "Health probe: $exe --health-check --urls $urls"
Write-Host ''
Write-Host "Start now: Start-ScheduledTask -TaskName '$resolvedTaskName'"
Write-Host "Load the client secret without printing it: `$token = [Environment]::GetEnvironmentVariable('${configurationPrefix}Token', 'User')"
Write-Host 'Rotate only the token: .\Install-Service.ps1 -RotateToken'
Write-Host "Add scopes without changing the token: .\Install-Service.ps1 -AddScopes @('eventpipe')"
Write-Host 'Uninstall: .\Install-Service.ps1 -Uninstall'
