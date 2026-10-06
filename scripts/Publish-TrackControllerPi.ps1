param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts\pi5' }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$projectPath = Join-Path $repoRoot 'src\TrackController\TrackController.Pi\TrackController.Pi.csproj'
& dotnet publish $projectPath -c Release -r linux-arm64 --self-contained true -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Pi publish failed.' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\TrackController-Pi5.md') -Destination (Join-Path $OutputDirectory 'SETUP.md') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\TrackController-Pi5.md') -Destination (Join-Path $OutputDirectory 'TrackController-Pi5.md') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\TrackController-Territories.md') -Destination (Join-Path $OutputDirectory 'TrackController-Territories.md') -Force
$archivePath = Join-Path (Split-Path -Parent $OutputDirectory) 'track-controller-pi5.tar.gz'
& tar -czf $archivePath -C $OutputDirectory .
if ($LASTEXITCODE -ne 0) { throw 'Pi archive creation failed.' }
Write-Output "Pi 5 package: $archivePath"
Write-Output 'See docs/TrackController-Pi5.md for Ethernet, SSH, startup, and LED setup.'
