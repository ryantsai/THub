[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$WebPackage,
    [Parameter(Mandatory = $true)][string]$WebDirectory,
    [Parameter(Mandatory = $true)][string]$WebAppPool,
    [Parameter(Mandatory = $true)][string]$PublicationsPackage,
    [Parameter(Mandatory = $true)][string]$PublicationsDirectory,
    [Parameter(Mandatory = $true)][string]$PublicationsAppPool,
    [Parameter(Mandatory = $true)][string]$WorkerPackage,
    [Parameter(Mandatory = $true)][string]$WorkerDirectory,
    [string]$WorkerServiceName = "THub Orchestration Worker",
    [Parameter(Mandatory = $true)][string]$BackupDirectory,
    [ValidateRange(5, 3600)][int]$StopTimeoutSeconds = 900,
    [ValidateRange(0, 300)][int]$IisDrainSeconds = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

Import-Module WebAdministration -ErrorAction Stop

function Resolve-ExistingFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
}

function Resolve-DeploymentDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ($fullPath -eq [System.IO.Path]::GetPathRoot($fullPath)) {
        throw "A deployment directory cannot be a drive root: '$fullPath'."
    }
    return $fullPath.TrimEnd("\")
}

function Wait-AppPoolStatus {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Status,
        [Parameter(Mandatory = $true)][DateTimeOffset]$Deadline
    )

    do {
        if ((Get-WebAppPoolState -Name $Name).Value -eq $Status) {
            return
        }
        Start-Sleep -Seconds 1
    } while ([DateTimeOffset]::UtcNow -lt $Deadline)

    throw "IIS application pool '$Name' did not reach '$Status' before the timeout."
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination `
        -Recurse -Force
}

function Clear-DirectoryContents {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (Test-Path -LiteralPath $Path -PathType Container) {
        Get-ChildItem -LiteralPath $Path -Force | Remove-Item -Recurse -Force
    }
}

function Assert-NotNested {
    param(
        [Parameter(Mandatory = $true)][string]$First,
        [Parameter(Mandatory = $true)][string]$Second
    )

    $firstWithSeparator = $First.TrimEnd("\") + "\"
    $secondWithSeparator = $Second.TrimEnd("\") + "\"
    if ($firstWithSeparator.StartsWith($secondWithSeparator, [StringComparison]::OrdinalIgnoreCase) -or
        $secondWithSeparator.StartsWith($firstWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Deployment and backup directories cannot contain one another: '$First', '$Second'."
    }
}

function Assert-PackageHash {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$RelativeManifestPath,
        [Parameter(Mandatory = $true)]$Manifest
    )

    $entry = $Manifest.packages | Where-Object { $_.file -eq $RelativeManifestPath }
    if (@($entry).Count -ne 1) {
        throw "Manifest must contain exactly one '$RelativeManifestPath' entry."
    }

    $actualHash = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash
    if (-not $actualHash.Equals([string]$entry.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "SHA-256 verification failed for '$RelativeManifestPath'."
    }
}

$manifestFile = Resolve-ExistingFile $ManifestPath
$webZip = Resolve-ExistingFile $WebPackage
$publicationsZip = Resolve-ExistingFile $PublicationsPackage
$workerZip = Resolve-ExistingFile $WorkerPackage
$webTarget = Resolve-DeploymentDirectory $WebDirectory
$publicationsTarget = Resolve-DeploymentDirectory $PublicationsDirectory
$workerTarget = Resolve-DeploymentDirectory $WorkerDirectory
$backupRoot = Resolve-DeploymentDirectory $BackupDirectory

$targets = @($webTarget, $publicationsTarget, $workerTarget, $backupRoot)
if (($targets | Select-Object -Unique).Count -ne $targets.Count) {
    throw "Web, Publications, Worker, and backup directories must be distinct."
}
for ($firstIndex = 0; $firstIndex -lt $targets.Count; $firstIndex++) {
    for ($secondIndex = $firstIndex + 1; $secondIndex -lt $targets.Count; $secondIndex++) {
        Assert-NotNested -First $targets[$firstIndex] -Second $targets[$secondIndex]
    }
}
foreach ($pool in @($WebAppPool, $PublicationsAppPool)) {
    if (-not (Test-Path -LiteralPath "IIS:\AppPools\$pool")) {
        throw "IIS application pool '$pool' does not exist."
    }
}

$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.application -ne "THub") {
    throw "The release manifest is not a supported THub manifest."
}
Assert-PackageHash -PackagePath $webZip -RelativeManifestPath "packages/THub.Web.zip" `
    -Manifest $manifest
Assert-PackageHash -PackagePath $publicationsZip `
    -RelativeManifestPath "packages/THub.Publications.zip" -Manifest $manifest
Assert-PackageHash -PackagePath $workerZip -RelativeManifestPath "packages/THub.Worker.zip" `
    -Manifest $manifest

$workerService = Get-Service -Name $WorkerServiceName -ErrorAction Stop
$workerState = $workerService.Status
$webState = (Get-WebAppPoolState -Name $WebAppPool).Value
$publicationsState = (Get-WebAppPoolState -Name $PublicationsAppPool).Value
if ($workerState -notin @("Running", "Stopped")) {
    throw "Worker service must be Running or Stopped before deployment; current state is '$workerState'."
}
if ($webState -notin @("Started", "Stopped") -or
    $publicationsState -notin @("Started", "Stopped")) {
    throw "IIS application pools must be Started or Stopped before deployment."
}
$initialWorkerRunning = $workerState -eq "Running"
$initialWebRunning = $webState -eq "Started"
$initialPublicationsRunning = $publicationsState -eq "Started"

$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("thub-release-" + [Guid]::NewGuid())
$backupName = "{0}-{1}" -f [DateTimeOffset]::UtcNow.ToString("yyyyMMdd-HHmmss"), `
    ([Guid]::NewGuid().ToString("N").Substring(0, 8))
$releaseBackup = Join-Path $backupRoot $backupName
$deploymentSucceeded = $false

try {
    New-Item -ItemType Directory -Path $stagingRoot, $releaseBackup -Force | Out-Null
    $staged = [ordered]@{
        Web = Join-Path $stagingRoot "web"
        Publications = Join-Path $stagingRoot "publications"
        Worker = Join-Path $stagingRoot "worker"
    }
    foreach ($path in $staged.Values) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }

    Expand-Archive -LiteralPath $webZip -DestinationPath $staged.Web
    Expand-Archive -LiteralPath $publicationsZip -DestinationPath $staged.Publications
    Expand-Archive -LiteralPath $workerZip -DestinationPath $staged.Worker

    foreach ($requiredFile in @(
        (Join-Path $staged.Web "THub.Web.exe"),
        (Join-Path $staged.Publications "THub.Publications.exe"),
        (Join-Path $staged.Worker "THub.Worker.exe")
    )) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "A package is missing required host binary '$requiredFile'."
        }
    }

    foreach ($target in @($webTarget, $publicationsTarget)) {
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $target "app_offline.htm") `
            -Value "THub is being updated. Please retry shortly." -Encoding UTF8
    }
    if ($IisDrainSeconds -gt 0) {
        Start-Sleep -Seconds $IisDrainSeconds
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StopTimeoutSeconds)
    if ($initialWebRunning) {
        Stop-WebAppPool -Name $WebAppPool
        Wait-AppPoolStatus -Name $WebAppPool -Status "Stopped" -Deadline $deadline
    }
    if ($initialPublicationsRunning) {
        Stop-WebAppPool -Name $PublicationsAppPool
        Wait-AppPoolStatus -Name $PublicationsAppPool -Status "Stopped" -Deadline $deadline
    }
    if ($initialWorkerRunning) {
        Stop-Service -Name $WorkerServiceName
        $workerService.WaitForStatus(
            [System.ServiceProcess.ServiceControllerStatus]::Stopped,
            [TimeSpan]::FromSeconds($StopTimeoutSeconds))
    }

    $deployments = @(
        @{ Name = "Web"; Source = $staged.Web; Target = $webTarget },
        @{ Name = "Publications"; Source = $staged.Publications; Target = $publicationsTarget },
        @{ Name = "Worker"; Source = $staged.Worker; Target = $workerTarget }
    )
    foreach ($deployment in $deployments) {
        $componentBackup = Join-Path $releaseBackup $deployment.Name
        if (Test-Path -LiteralPath $deployment.Target -PathType Container) {
            Copy-DirectoryContents -Source $deployment.Target -Destination $componentBackup
        }
        Clear-DirectoryContents -Path $deployment.Target
        Copy-DirectoryContents -Source $deployment.Source -Destination $deployment.Target
    }

    if ($initialWorkerRunning) {
        Start-Service -Name $WorkerServiceName
        $workerService.WaitForStatus(
            [System.ServiceProcess.ServiceControllerStatus]::Running,
            [TimeSpan]::FromSeconds($StopTimeoutSeconds))
    }
    if ($initialPublicationsRunning) {
        Start-WebAppPool -Name $PublicationsAppPool
        Wait-AppPoolStatus -Name $PublicationsAppPool -Status "Started" `
            -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($StopTimeoutSeconds))
    }
    if ($initialWebRunning) {
        Start-WebAppPool -Name $WebAppPool
        Wait-AppPoolStatus -Name $WebAppPool -Status "Started" `
            -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($StopTimeoutSeconds))
    }

    $deploymentSucceeded = $true
    Write-Output "THub hosts were updated. Previous files are backed up at '$releaseBackup'."
}
finally {
    if (-not $deploymentSucceeded) {
        Write-Warning "Deployment did not complete. Hosts are left stopped where possible; restore from '$releaseBackup' after reviewing the failure."
    }
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
