param(
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z_][a-zA-Z0-9_-]*$')][string]$User,
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9.-]*$')][string]$PiAddress = '192.168.50.2',
    [ValidateRange(1,65535)][int]$LocalPort = 11405,
    [ValidateRange(1,65535)][int]$RemotePort = 11405
)
$ErrorActionPreference = 'Stop'
$forward = "127.0.0.1:${LocalPort}:127.0.0.1:${RemotePort}"
Write-Output "Opening the Pi tunnel at 127.0.0.1:$LocalPort. Keep this terminal open. Ctrl+C disconnects."
& ssh -N -L $forward -o ExitOnForwardFailure=yes -o ServerAliveInterval=5 -o ServerAliveCountMax=2 "$User@$PiAddress"
if ($LASTEXITCODE -ne 0) { throw 'SSH tunnel ended or could not be opened. Check Ethernet, SSH login, and the Pi runtime.' }
