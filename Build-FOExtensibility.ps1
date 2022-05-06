$ErrorActionPreference = "Stop"

$Configuration="Release"
[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

try 
{
    Push-Location $scriptPath

    Remove-Item $scriptPath\bin\release\FabricObserver.Extensibility\ -Recurse -Force -EA SilentlyContinue

    dotnet publish FabricObserver.Extensibility\FabricObserver.Extensibility.csproj -o bin\release\FabricObserver.Extensibility
}
finally 
{
    Pop-Location
}