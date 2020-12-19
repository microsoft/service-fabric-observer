$ErrorActionPreference = "Stop"

$Configuration="Release"
[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

try {
    Push-Location $scriptPath

    Remove-Item $scriptPath\bin\release\FabricObserverWeb\ -Recurse -Force -EA SilentlyContinue

    dotnet publish FabricObserverWeb\FabricObserverWeb.csproj -o bin\release\FabricObserverWeb\linux-x64\self-contained\FabricObserverWebApiType\FabricObserverWebPkg\Code -c $Configuration -r linux-x64 --self-contained true
    dotnet publish FabricObserverWeb\FabricObserverWeb.csproj -o bin\release\FabricObserverWeb\linux-x64\framework-dependent\FabricObserverWebApiType\FabricObserverWebPkg\Code -c $Configuration -r linux-x64 --self-contained false
    dotnet publish FabricObserverWeb\FabricObserverWeb.csproj -o bin\release\FabricObserverWeb\win-x64\self-contained\FabricObserverWebApiType\FabricObserverWebPkg\Code -c $Configuration -r win-x64 --self-contained true
    dotnet publish FabricObserverWeb\FabricObserverWeb.csproj -o bin\release\FabricObserverWeb\win-x64\framework-dependent\FabricObserverWebApiType\FabricObserverWebPkg\Code -c $Configuration -r win-x64 --self-contained false

    Copy-Item FabricObserverWeb\PackageRoot\* bin\release\FabricObserverWeb\linux-x64\self-contained\FabricObserverWebApiType\FabricObserverWebPkg\ -Recurse
    Copy-Item FabricObserverWeb\PackageRoot\* bin\release\FabricObserverWeb\linux-x64\framework-dependent\FabricObserverWebApiType\FabricObserverWebPkg\ -Recurse

    Copy-Item FabricObserverWeb\PackageRoot\* bin\release\FabricObserverWeb\win-x64\self-contained\FabricObserverWebApiType\FabricObserverWebPkg\ -Recurse
    Copy-Item FabricObserverWeb\PackageRoot\* bin\release\FabricObserverWeb\win-x64\framework-dependent\FabricObserverWebApiType\FabricObserverWebPkg\ -Recurse

    Copy-Item FabricObserverWebApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricObserverWeb\linux-x64\self-contained\FabricObserverWebApiType\ApplicationManifest.xml
    Copy-Item FabricObserverWebApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricObserverWeb\linux-x64\framework-dependent\FabricObserverWebApiType\ApplicationManifest.xml
    Copy-Item FabricObserverWebApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricObserverWeb\win-x64\self-contained\FabricObserverWebApiType\ApplicationManifest.xml
    Copy-Item FabricObserverWebApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricObserverWeb\win-x64\framework-dependent\FabricObserverWebApiType\ApplicationManifest.xml
}
finally {
    Pop-Location
}