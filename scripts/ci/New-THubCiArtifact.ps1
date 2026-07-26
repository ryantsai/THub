[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Test", "Production")]
    [string]$Lane,

    [ValidateSet("Release")]
    [string]$Configuration = "Release",

    [ValidatePattern("^win-[a-z0-9]+$")]
    [string]$RuntimeIdentifier = "win-x64",

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$WorkDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..")).Path
$solutionPath = Join-Path $repositoryRoot "THub.slnx"
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
$resolvedWork = [System.IO.Path]::GetFullPath($WorkDirectory)

function Assert-SafeBuildDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $root = [System.IO.Path]::GetPathRoot($Path)
    if ($Path -eq $root -or $Path -eq $repositoryRoot) {
        throw "$Name must not be a drive root or the repository root."
    }

    $pathWithSeparator = $Path.TrimEnd("\") + "\"
    $repositoryWithSeparator = $repositoryRoot.TrimEnd("\") + "\"
    if ($repositoryWithSeparator.StartsWith(
        $pathWithSeparator,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name must not contain the repository."
    }
}

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function New-ZipPackage {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }

    Compress-Archive -Path (Join-Path $SourceDirectory "*") -DestinationPath $DestinationPath `
        -CompressionLevel Optimal
}

Assert-SafeBuildDirectory -Path $resolvedOutput -Name "OutputDirectory"
Assert-SafeBuildDirectory -Path $resolvedWork -Name "WorkDirectory"
$outputWithSeparator = $resolvedOutput.TrimEnd("\") + "\"
$workWithSeparator = $resolvedWork.TrimEnd("\") + "\"
if ($outputWithSeparator.StartsWith($workWithSeparator, [StringComparison]::OrdinalIgnoreCase) -or
    $workWithSeparator.StartsWith($outputWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory and WorkDirectory must not contain one another."
}

foreach ($directory in @($resolvedOutput, $resolvedWork)) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $directory | Out-Null
}

$buildRoot = Join-Path $resolvedWork "build"
$publishRoot = Join-Path $resolvedWork "publish"
$testResults = Join-Path $resolvedWork "test-results"
$packages = Join-Path $resolvedOutput "packages"
$release = Join-Path $resolvedOutput "release"
New-Item -ItemType Directory -Path $buildRoot, $publishRoot, $testResults, $packages, $release `
    -Force | Out-Null

Push-Location $repositoryRoot
try {
    Invoke-DotNet -Arguments @("--info")
    Invoke-DotNet -Arguments @("tool", "restore")
    Invoke-DotNet -Arguments @(
        "restore", $solutionPath,
        "--runtime", $RuntimeIdentifier
    )
    Invoke-DotNet -Arguments @(
        "build", $solutionPath,
        "--configuration", $Configuration,
        "--no-restore",
        "--artifacts-path", $buildRoot
    )
    Invoke-DotNet -Arguments @(
        "test", $solutionPath,
        "--configuration", $Configuration,
        "--no-build",
        "--no-restore",
        "--artifacts-path", $buildRoot,
        "--results-directory", $testResults,
        "--logger", "trx"
    )

    $projects = [ordered]@{
        "THub.Web" = "src\THub.Web\THub.Web.csproj"
        "THub.Publications" = "src\THub.Publications\THub.Publications.csproj"
        "THub.Worker" = "src\THub.Worker\THub.Worker.csproj"
    }

    foreach ($project in $projects.GetEnumerator()) {
        $publishDirectory = Join-Path $publishRoot $project.Key
        Invoke-DotNet -Arguments @(
            "publish", (Join-Path $repositoryRoot $project.Value),
            "--configuration", $Configuration,
            "--runtime", $RuntimeIdentifier,
            "--self-contained", "false",
            "--no-restore",
            "--artifacts-path", $buildRoot,
            "--output", $publishDirectory
        )

        $developmentSettings = Get-ChildItem -LiteralPath $publishDirectory `
            -Filter "appsettings.Development.json" -File -Recurse -ErrorAction SilentlyContinue
        if ($developmentSettings) {
            throw "Development settings were found in the $($project.Key) publish output."
        }

        New-ZipPackage -SourceDirectory $publishDirectory `
            -DestinationPath (Join-Path $packages "$($project.Key).zip")
    }

    Copy-Item -LiteralPath (Join-Path $repositoryRoot "scripts\release\Update-THubHosts.ps1") `
        -Destination $release
    Copy-Item -LiteralPath (Join-Path $repositoryRoot "scripts\release\README.md") `
        -Destination $release

    $packageEntries = foreach ($package in Get-ChildItem -LiteralPath $packages -Filter "*.zip" -File) {
        $hash = Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256
        [ordered]@{
            file = "packages/$($package.Name)"
            sha256 = $hash.Hash.ToLowerInvariant()
            bytes = $package.Length
        }
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        application = "THub"
        lane = $Lane
        configuration = $Configuration
        runtimeIdentifier = $RuntimeIdentifier
        buildNumber = $env:BUILD_BUILDNUMBER
        buildId = $env:BUILD_BUILDID
        sourceVersion = $env:BUILD_SOURCEVERSION
        createdUtc = [DateTimeOffset]::UtcNow.ToString("O")
        packages = @($packageEntries)
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content `
        -LiteralPath (Join-Path $resolvedOutput "manifest.json") -Encoding UTF8
}
finally {
    Pop-Location
}

Write-Output "Created $Lane CI artifact at '$resolvedOutput'."
