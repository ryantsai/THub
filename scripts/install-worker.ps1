param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,
    [string]$ServiceName = "THub Orchestration Worker",
    [string]$Credential
)

$resolvedDirectory = (Resolve-Path -LiteralPath $PublishDirectory -ErrorAction Stop).Path
$workerPath = Join-Path $resolvedDirectory "THub.Worker.exe"
if (-not (Test-Path -LiteralPath $workerPath -PathType Leaf)) {
    throw "THub.Worker.exe was not found in '$resolvedDirectory'. Publish the worker first."
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "A service named '$ServiceName' already exists. This installer will not overwrite it."
}

$parameters = @{
    Name = $ServiceName
    BinaryPathName = ('"{0}"' -f $workerPath)
    DisplayName = $ServiceName
    Description = "Durable scheduler and workflow execution host for THub."
    StartupType = "Automatic"
}

if ($Credential) {
    $parameters.Credential = Get-Credential -UserName $Credential -Message "Enter the THub worker service account password"
}

New-Service @parameters
Write-Output "Installed '$ServiceName'. Review its SQL/file permissions, then start it with Start-Service."
