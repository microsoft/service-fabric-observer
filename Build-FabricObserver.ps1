$ErrorActionPreference = "Stop"

$Configuration="Release"
[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

try 
{
    Push-Location $scriptPath

    Remove-Item $scriptPath\bin\release\FabricObserver\ -Recurse -Force -EA SilentlyContinue

    dotnet publish FabricObserver\FabricObserver.csproj -o bin\release\FabricObserver\linux-x64\self-contained\FabricObserverType\FabricObserverPkg\Code -c $Configuration -r linux-x64 --self-contained true
    dotnet publish FabricObserver\FabricObserver.csproj -o bin\release\FabricObserver\linux-x64\framework-dependent\FabricObserverType\FabricObserverPkg\Code -c $Configuration -r linux-x64 --self-contained false
    dotnet publish FabricObserver\FabricObserver.csproj -o bin\release\FabricObserver\win-x64\self-contained\FabricObserverType\FabricObserverPkg\Code -c $Configuration -r win-x64 --self-contained true
    dotnet publish FabricObserver\FabricObserver.csproj -o bin\release\FabricObserver\win-x64\framework-dependent\FabricObserverType\FabricObserverPkg\Code -c $Configuration -r win-x64 --self-contained false

    Copy-Item FabricObserver\PackageRoot\* bin\release\FabricObserver\linux-x64\self-contained\FabricObserverType\FabricObserverPkg\ -Recurse
    Copy-Item FabricObserver\PackageRoot\* bin\release\FabricObserver\linux-x64\framework-dependent\FabricObserverType\FabricObserverPkg\ -Recurse

    Copy-Item FabricObserver\PackageRoot\* bin\release\FabricObserver\win-x64\self-contained\FabricObserverType\FabricObserverPkg\ -Recurse
    Copy-Item FabricObserver\PackageRoot\* bin\release\FabricObserver\win-x64\framework-dependent\FabricObserverType\FabricObserverPkg\ -Recurse

    # ApplicationManifest - All
    Copy-Item FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricObserver\linux-x64\self-contained\FabricObserverType\ApplicationManifest.xml
    Copy-Item FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricObserver\linux-x64\framework-dependent\FabricObserverType\ApplicationManifest.xml
    Copy-Item FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricObserver\win-x64\self-contained\FabricObserverType\ApplicationManifest.xml
    Copy-Item FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricObserver\win-x64\framework-dependent\FabricObserverType\ApplicationManifest.xml

    # ServiceManifest - Linux
    Copy-Item FabricObserver\PackageRoot\ServiceManifest_linux.xml bin\release\FabricObserver\linux-x64\framework-dependent\FabricObserverType\FabricObserverPkg\ServiceManifest.xml -Force -Confirm:$False
    Copy-Item FabricObserver\PackageRoot\ServiceManifest_linux.xml bin\release\FabricObserver\linux-x64\self-contained\FabricObserverType\FabricObserverPkg\ServiceManifest.xml -Force -Confirm:$False
}
finally 
{
    Pop-Location
}