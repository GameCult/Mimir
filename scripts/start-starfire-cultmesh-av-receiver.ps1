param(
    [string]$RelayHost = "10.77.0.1",
    [int]$RelayPort = 3075,
    [string]$Stream = "raven-primary-av",
    [string]$Udp = "127.0.0.1:5200"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\Mimir.CultMeshMedia\Mimir.CultMeshMedia.csproj"
dotnet run --project $project -- recv --host $RelayHost --port $RelayPort --stream $Stream --udp $Udp
