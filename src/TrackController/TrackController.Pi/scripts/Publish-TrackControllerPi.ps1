param([string]$OutputDirectory, [switch]$Archive)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$moduleRoot = Split-Path -Parent $projectRoot

if (-not $OutputDirectory)
{
    $repositoryRoot = Split-Path -Parent (Split-Path -Parent $moduleRoot)
    $OutputDirectory = Join-Path $repositoryRoot 'publish\track-controller-pi'
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$projectPath = Join-Path $projectRoot 'TrackController.Pi.csproj'
& dotnet publish $projectPath -c Release -r linux-arm64 --self-contained true -p:PublishReadyToRun=true -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0)
{
    throw 'Pi publish failed.'
}

$documentationRoot = Join-Path $OutputDirectory 'docs'
foreach ($projectName in @('TrackController.Pi', 'TrackController.Runtime', 'TrackController.Wpf', 'TrackController.TestUI.Wpf'))
{
    $documentationPath = Join-Path $documentationRoot $projectName
    New-Item -ItemType Directory -Path $documentationPath -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $moduleRoot "$projectName\README.md") -Destination (Join-Path $documentationPath 'README.md') -Force
}

$plcDocumentation = Join-Path $documentationRoot 'TrackController.Core\Control'
New-Item -ItemType Directory -Path $plcDocumentation -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $moduleRoot 'TrackController.Core\Control\README.md') -Destination (Join-Path $plcDocumentation 'README.md') -Force

if ($Archive)
{
    $archivePath = Join-Path (Split-Path -Parent $OutputDirectory) 'track-controller-pi5.tar.gz'
    & tar -czf $archivePath -C $OutputDirectory .
    if ($LASTEXITCODE -ne 0) { throw 'Pi archive creation failed.' }
    Write-Output "Optional archive: $archivePath"
}

Write-Output "Track Controller Pi folder: $OutputDirectory"
Write-Output 'Copy this folder''s contents into ~/track-controller on the Pi, separate from ~/train-controller.'
Write-Output 'See docs/TrackController.Pi/README.md in that folder for connection, startup, and LED setup.'
