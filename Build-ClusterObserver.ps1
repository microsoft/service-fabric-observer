param(
    [string] $RuntimeId = "win-x64",
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"

[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition
try {
    Push-Location $scriptPath

    Remove-Item $scriptPath\bin\release\ClusterObserver\$RuntimeId\ -Recurse -Force -EA SilentlyContinue

    dotnet publish ClusterObserver\ClusterObserver.csproj -o bin\release\ClusterObserver\$RuntimeId\self-contained\ClusterObserverType\ClusterObserverPkg\Code -c $Configuration -r $RuntimeId --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "ClusterObserver self-contained publish failed for $RuntimeId." }
    dotnet publish ClusterObserver\ClusterObserver.csproj -o bin\release\ClusterObserver\$RuntimeId\framework-dependent\ClusterObserverType\ClusterObserverPkg\Code -c $Configuration -r $RuntimeId --self-contained false
    if ($LASTEXITCODE -ne 0) { throw "ClusterObserver framework-dependent publish failed for $RuntimeId." }
    

    Copy-Item ClusterObserver\PackageRoot\* bin\release\ClusterObserver\$RuntimeId\self-contained\ClusterObserverType\ClusterObserverPkg\ -Recurse
    Copy-Item ClusterObserver\PackageRoot\* bin\release\ClusterObserver\$RuntimeId\framework-dependent\ClusterObserverType\ClusterObserverPkg\ -Recurse

    Copy-Item ClusterObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\ClusterObserver\$RuntimeId\self-contained\ClusterObserverType\ApplicationManifest.xml
    Copy-Item ClusterObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\ClusterObserver\$RuntimeId\framework-dependent\ClusterObserverType\ApplicationManifest.xml
}
finally {
    Pop-Location
}
