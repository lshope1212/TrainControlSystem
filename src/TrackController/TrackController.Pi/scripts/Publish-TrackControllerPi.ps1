param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$moduleRoot = Split-Path -Parent $projectRoot

if (-not $OutputDirectory)
{
    $OutputDirectory = Join-Path $projectRoot 'artifacts\pi5'
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$projectPath = Join-Path $projectRoot 'TrackController.Pi.csproj'
& dotnet publish $projectPath -c Release -r linux-arm64 --self-contained true -o $OutputDirectory --nologo
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

$archivePath = Join-Path (Split-Path -Parent $OutputDirectory) 'track-controller-pi5.tar.gz'
& tar -czf $archivePath -C $OutputDirectory .
if ($LASTEXITCODE -ne 0)
{
    throw 'Pi archive creation failed.'
}

Write-Output "Pi 5 package: $archivePath"
Write-Output 'See docs/TrackController.Pi/README.md in the package for Ethernet, SSH, startup, and LED setup.'
