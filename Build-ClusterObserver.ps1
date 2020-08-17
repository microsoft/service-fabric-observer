$ErrorActionPreference = "Stop"

$Configuration="Release"
[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

try {
    Push-Location $scriptPath

    Remove-Item $scriptPath\bin\release\ClusterObserver\ -Recurse -Force -EA SilentlyContinue

    dotnet publish ClusterObserver\ClusterObserver.csproj -o bin\release\ClusterObserver\linux-x64\self-contained\ClusterObserverType\ClusterObserverPkg\Code -c $Configuration -r linux-x64 --self-contained true
    dotnet publish ClusterObserver\ClusterObserver.csproj -o bin\release\ClusterObserver\linux-x64\framework-dependent\ClusterObserverType\ClusterObserverPkg\Code -c $Configuration -r linux-x64 --self-contained false
    dotnet publish ClusterObserver\ClusterObserver.csproj -o bin\release\ClusterObserver\win-x64\self-contained\ClusterObserverType\ClusterObserverPkg\Code -c $Configuration -r win-x64 --self-contained true
    dotnet publish ClusterObserver\ClusterObserver.csproj -o bin\release\ClusterObserver\win-x64\framework-dependent\ClusterObserverType\ClusterObserverPkg\Code -c $Configuration -r win-x64 --self-contained false

    Copy-Item ClusterObserver\PackageRoot\* bin\release\ClusterObserver\linux-x64\self-contained\ClusterObserverType\ClusterObserverPkg\ -Recurse
    Copy-Item ClusterObserver\PackageRoot\* bin\release\ClusterObserver\linux-x64\framework-dependent\ClusterObserverType\ClusterObserverPkg\ -Recurse

    Copy-Item ClusterObserver\PackageRoot\* bin\release\ClusterObserver\win-x64\self-contained\ClusterObserverType\ClusterObserverPkg\ -Recurse
    Copy-Item ClusterObserver\PackageRoot\* bin\release\ClusterObserver\win-x64\framework-dependent\ClusterObserverType\ClusterObserverPkg\ -Recurse

    Copy-Item ClusterObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\ClusterObserver\linux-x64\self-contained\ClusterObserverType\ApplicationManifest.xml
    Copy-Item ClusterObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\ClusterObserver\linux-x64\framework-dependent\ClusterObserverType\ApplicationManifest.xml
    Copy-Item ClusterObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\ClusterObserver\win-x64\self-contained\ClusterObserverType\ApplicationManifest.xml
    Copy-Item ClusterObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\ClusterObserver\win-x64\framework-dependent\ClusterObserverType\ApplicationManifest.xml
}
finally {
    Pop-Location
}
