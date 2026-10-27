@echo off
setlocal
pushd "%~dp0"
dotnet build TrainControlSystem.sln
if errorlevel 1 (
    echo Build failed. Close running Track Model windows before rebuilding.
    popd
    pause
    exit /b 1
)
start "" "%~dp0src\TrackModel\TrackModel.Wpf\bin\Debug\net10.0-windows\TrackModel.Wpf.exe"
start "" "%~dp0src\TrackModel\TrackModel.TestUI.Wpf\bin\Debug\net10.0-windows\TrackModel.TestUI.Wpf.exe"
popd
