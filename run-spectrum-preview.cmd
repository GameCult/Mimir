@echo off
setlocal

set "ROOT=%~dp0"
set "APP=%ROOT%src\Mimir.App\bin\Debug\net10.0\Mimir.App.exe"

if not exist "%APP%" (
    echo Mimir.App.exe was not found.
    echo Build it first with:
    echo   dotnet build "%ROOT%src\Mimir.App\Mimir.App.csproj"
    pause
    exit /b 1
)

set "MIMIR_SYNTHETIC_SPECTRUM_PREVIEW=1"
set "MIMIR_SPECTRUM_INTERVAL_SECONDS=0.016"
set "MIMIR_SPECTRUM_SOURCE_LANES=8"

start "Mimir Spectrum Preview" "%APP%"
