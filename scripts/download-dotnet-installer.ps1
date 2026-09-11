#requires -Version 7.4

function Test-TransientInstallerTransportError {
    param([System.Exception] $Exception)

    $transient = $false
    for ($error = $Exception; $null -ne $error; $error = $error.InnerException) {
        if ($error -is [System.Security.Authentication.AuthenticationException]) {
            return $false
        }
        if ($error -is [System.Net.Http.HttpRequestException]) {
            if ($null -ne $error.StatusCode) { return $false }
            $transient = $transient -or $error.HttpRequestError -in @(
                'ConnectionError', 'NameResolutionError', 'ResponseEnded')
        }
        if ($error -is [System.Net.Http.HttpIOException]) {
            $transient = $transient -or $error.HttpRequestError -eq 'ResponseEnded'
        }
        if ($error -is [System.Net.Sockets.SocketException]) {
            $transient = $transient -or $error.SocketErrorCode -in @(
                'ConnectionAborted', 'ConnectionReset', 'ConnectionRefused',
                'NetworkDown', 'NetworkReset', 'NetworkUnreachable',
                'HostDown', 'HostUnreachable', 'TryAgain', 'TimedOut')
        }
    }
    return $transient
}

function Save-DotnetInstallScript {
    param([string] $OutFile = 'dotnet-install.ps1')

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Invoke-WebRequest -Uri https://dot.net/v1/dotnet-install.ps1 -OutFile $OutFile `
                -ConnectionTimeoutSeconds 30 -OperationTimeoutSeconds 60 -ErrorAction Stop
            return
        }
        catch {
            # Built-in HTTP-status retries do not cover interrupted response bodies.
            if ($attempt -eq 3 -or -not (Test-TransientInstallerTransportError $_.Exception)) {
                throw
            }
            $delay = 2 * $attempt
            Write-Warning "Installer download transport failure (attempt $attempt/3); retrying in ${delay}s."
            Start-Sleep -Seconds $delay
        }
    }
}
