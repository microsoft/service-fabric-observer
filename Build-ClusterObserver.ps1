param(
    [string] $RuntimeId = "win-x64",
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"

[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition
[string] $winArmSFPackageRefOverride = "/p:VersionOverride_SFServices=7.0.1816"

# For SF 11/12 arm64 builds, today we need to override the SF package reference version to match the current version of the SDK
# to ensure arm64 x64 emulation works correctly
if($RuntimeId -eq "win-arm64") {
    $winArmSFPackageRefOverride = "/p:IsArmTarget=true"
}
try {
    Push-Location $scriptPath

    Remove-Item $scriptPath\bin\release\ClusterObserver\ -Recurse -Force -EA SilentlyContinue

    dotnet publish ClusterObserver\ClusterObserver.csproj $winArmSFPackageRefOverride -o bin\release\ClusterObserver\$RuntimeId\self-contained\ClusterObserverType\ClusterObserverPkg\Code -c $Configuration -r $RuntimeId --self-contained true
    dotnet publish ClusterObserver\ClusterObserver.csproj $winArmSFPackageRefOverride -o bin\release\ClusterObserver\$RuntimeId\framework-dependent\ClusterObserverType\ClusterObserverPkg\Code -c $Configuration -r $RuntimeId --self-contained false
    

    Copy-Item ClusterObserver\PackageRoot\* bin\release\ClusterObserver\$RuntimeId\self-contained\ClusterObserverType\ClusterObserverPkg\ -Recurse
    Copy-Item ClusterObserver\PackageRoot\* bin\release\ClusterObserver\$RuntimeId\framework-dependent\ClusterObserverType\ClusterObserverPkg\ -Recurse

    Copy-Item ClusterObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\ClusterObserver\$RuntimeId\self-contained\ClusterObserverType\ApplicationManifest.xml
    Copy-Item ClusterObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\ClusterObserver\$RuntimeId\framework-dependent\ClusterObserverType\ApplicationManifest.xml
}
finally {
    Pop-Location
}
