<#
.SYNOPSIS
    Development-only input harness: sends one TrackModelTrainUpdateMessage to the running
    production Track Model, the way the Train Model will report a train's block.

.DESCRIPTION
    Send-only. It connects to the Track Model's inbound pipe as a client, writes one envelope in
    the shared format (see TrainControl.Common/Communication/MessageSerializer) and disconnects.
    It hosts no pipe, so it never competes with the real CTC, Track Controller or any TestUI.

    The Track Model applies the update exactly as it would from the Train Model: the train's
    previous block clears, the new block becomes occupied, and the measured occupancy flows
    Track Model -> Track Controller -> CTC.

.PARAMETER TrainId
    Train label. The Track Model demonstration starts with train "01" in block 1; CTC-dispatched
    trains use the canonical three-digit IDs ("000", "001", ...).

.PARAMETER Block
    Block the train is now in (Track Model block ID, e.g. "2"). Omit, or use -Remove, to take the
    train off the track.

.PARAMETER Remove
    Removes the train from the track (sends an empty CurrentBlockId).

.PARAMETER SpeedMetersPerSecond
    Actual speed to report; 0 by default.

.PARAMETER Pipe
    Track Model inbound pipe; the production name by default.

.EXAMPLE
    .\tools\Send-TrackModelTrainUpdate.ps1 -TrainId 01 -Block 2

.EXAMPLE
    .\tools\Send-TrackModelTrainUpdate.ps1 -TrainId 01 -Remove
#>
[CmdletBinding(DefaultParameterSetName = 'Move')]
param(
    [Parameter(Mandatory = $true)]
    [string] $TrainId,

    [Parameter(ParameterSetName = 'Move', Mandatory = $true)]
    [string] $Block,

    [Parameter(ParameterSetName = 'Remove', Mandatory = $true)]
    [switch] $Remove,

    [double] $SpeedMetersPerSecond = 0,

    [string] $Pipe = 'TrainControl.TrackModel'
)

$ErrorActionPreference = 'Stop'

$payload = [ordered]@{
    trainId                    = $TrainId
    currentBlockId             = $(if ($Remove) { '' } else { $Block })
    actualSpeedMetersPerSecond = $SpeedMetersPerSecond
    boardingPassengers         = 0
    disembarkingPassengers     = 0
    exchangeId                 = ''
}
$envelope = [ordered]@{ messageType = 'TrackModelTrainUpdateMessage'; payload = $payload }
$json = $envelope | ConvertTo-Json -Compress -Depth 4

$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $Pipe, [System.IO.Pipes.PipeDirection]::Out)
try {
    try {
        $client.Connect(2500)
    }
    catch [System.TimeoutException] {
        throw "The Track Model is not running (no server on pipe '$Pipe'). Start TrackModel.Wpf first."
    }

    $writer = New-Object System.IO.StreamWriter($client, (New-Object System.Text.UTF8Encoding($false)))
    $writer.WriteLine($json)
    $writer.Flush()
    Write-Host "Sent to ${Pipe}: $json"
}
finally {
    $client.Dispose()
}
