$ErrorActionPreference = "Stop"

$Configuration="Release"
[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

try 
{
    Push-Location $scriptPath

    Remove-Item $scriptPath\bin\release\FabricObserver.Extensibility\ -Recurse -Force -EA SilentlyContinue

    foreach ($framework in @("net8.0", "net10.0"))
    {
        dotnet publish FabricObserver.Extensibility\FabricObserver.Extensibility.csproj -c $Configuration -f $framework -o bin\release\FabricObserver.Extensibility\$framework
        if ($LASTEXITCODE -ne 0) { throw "Extensibility publish failed for $framework." }
    }
}
finally 
{
    Pop-Location
}