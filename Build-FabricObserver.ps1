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
    $winArmSFPackageRefOverride = "/p:Version_SFServices=8.0.2707"
}

try 
{
    Push-Location $scriptPath

    Remove-Item $scriptPath\bin\release\FabricObserver\$RuntimeId\ -Recurse -Force -EA SilentlyContinue

    dotnet publish FabricObserver\FabricObserver.csproj $winArmSFPackageRefOverride -o bin\release\FabricObserver\$RuntimeId\self-contained\FabricObserverType\FabricObserverPkg\Code -c $Configuration -r $RuntimeId --self-contained true
    dotnet publish FabricObserver\FabricObserver.csproj $winArmSFPackageRefOverride -o bin\release\FabricObserver\$RuntimeId\framework-dependent\FabricObserverType\FabricObserverPkg\Code -c $Configuration -r $RuntimeId --self-contained false

    Copy-Item FabricObserver\PackageRoot\* bin\release\FabricObserver\$RuntimeId\self-contained\FabricObserverType\FabricObserverPkg\ -Recurse
    Copy-Item FabricObserver\PackageRoot\* bin\release\FabricObserver\$RuntimeId\framework-dependent\FabricObserverType\FabricObserverPkg\ -Recurse

    # ApplicationManifest - All
    Copy-Item FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricObserver\$RuntimeId\self-contained\FabricObserverType\ApplicationManifest.xml
    Copy-Item FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricObserver\$RuntimeId\framework-dependent\FabricObserverType\ApplicationManifest.xml

    if($RuntimeId -eq "linux-x64") {
        # ServiceManifest - Linux
        Copy-Item FabricObserver\PackageRoot\ServiceManifest_linux.xml bin\release\FabricObserver\$RuntimeId\framework-dependent\FabricObserverType\FabricObserverPkg\ServiceManifest.xml -Force -Confirm:$False
        Copy-Item FabricObserver\PackageRoot\ServiceManifest_linux.xml bin\release\FabricObserver\$RuntimeId\self-contained\FabricObserverType\FabricObserverPkg\ServiceManifest.xml -Force -Confirm:$False

        # Get rid of ServiceManifest_linux.xml from build output.
        Remove-Item bin\release\FabricObserver\$RuntimeId\framework-dependent\FabricObserverType\FabricObserverPkg\ServiceManifest_linux.xml -Force -Confirm:$False
        Remove-Item bin\release\FabricObserver\$RuntimeId\self-contained\FabricObserverType\FabricObserverPkg\ServiceManifest_linux.xml -Force -Confirm:$False
    }
}
finally 
{
    Pop-Location
}