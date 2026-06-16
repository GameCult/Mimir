param(
    [string]$Cache = "C:\Meta\Mimir\state\mimir-cultmesh-media.cc"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\Mimir.CultMeshMedia\Mimir.CultMeshMedia.csproj"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Cache) | Out-Null
dotnet run --project $project -- relay --cache $Cache
