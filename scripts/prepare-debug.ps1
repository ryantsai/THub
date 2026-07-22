$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command,
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$CommandArguments
    )

    & $Command @CommandArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command '$Command $($CommandArguments -join ' ')' failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repositoryRoot
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw ".NET SDK was not found on PATH. Install the .NET 10 SDK."
    }

    if (-not (Get-Command sqllocaldb -ErrorAction SilentlyContinue)) {
        throw "SQL Server LocalDB was not found. Install SQL Server Express LocalDB or the Visual Studio data workload."
    }

    Write-Host "[THub] Restoring local .NET tools..." -ForegroundColor Cyan
    Invoke-CheckedCommand dotnet tool restore

    Write-Host "[THub] Restoring solution packages..." -ForegroundColor Cyan
    Invoke-CheckedCommand dotnet restore THub.slnx

    Write-Host "[THub] Starting MSSQLLocalDB..." -ForegroundColor Cyan
    Invoke-CheckedCommand sqllocaldb start MSSQLLocalDB

    Write-Host "[THub] Building Debug binaries..." -ForegroundColor Cyan
    Invoke-CheckedCommand dotnet build THub.slnx --configuration Debug --no-restore

    Write-Host "[THub] Applying migrations to LocalDB database THub.Debug..." -ForegroundColor Cyan
    $previousAspNetCoreEnvironment = $env:ASPNETCORE_ENVIRONMENT
    $env:ASPNETCORE_ENVIRONMENT = "Development"
    try {
        Invoke-CheckedCommand dotnet tool run dotnet-ef database update `
            --project src/THub.Infrastructure `
            --startup-project src/THub.Web `
            --no-build
    }
    finally {
        $env:ASPNETCORE_ENVIRONMENT = $previousAspNetCoreEnvironment
    }

    Write-Host "[THub] Debug environment is ready." -ForegroundColor Green
}
finally {
    Pop-Location
}

