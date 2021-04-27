[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

function Build-SFPkg {
    param (
        [string]
        $packageId,

        [string]
        $basePath
    )

    $ProgressPreference = "SilentlyContinue"

    [string] $outputDir = "$scriptPath\bin\release\FabricObserverWeb\SFPkgs"
    [string] $zipPath = "$outputDir\$($packageId).zip"
    [System.IO.Directory]::CreateDirectory($outputDir) | Out-Null

    Compress-Archive "$basePath\*"  $zipPath -Force

    Move-Item -Path $zipPath -Destination ($zipPath.Replace(".zip", ".sfpkg"))
}

try {
    Push-Location $scriptPath

    Build-SFPkg "Microsoft.ServiceFabricApps.FabricObserverWeb.Linux.SelfContained.2.0.2" "$scriptPath\bin\release\FabricObserverWeb\linux-x64\self-contained\FabricObserverWebApiType"
    Build-SFPkg "Microsoft.ServiceFabricApps.FabricObserverWeb.Linux.FrameworkDependent.2.0.2" "$scriptPath\bin\release\FabricObserverWeb\linux-x64\framework-dependent\FabricObserverWebApiType"

    Build-SFPkg "Microsoft.ServiceFabricApps.FabricObserverWeb.Windows.SelfContained.2.0.2" "$scriptPath\bin\release\FabricObserverWeb\win-x64\self-contained\FabricObserverWebApiType"
    Build-SFPkg "Microsoft.ServiceFabricApps.FabricObserverWeb.Windows.FrameworkDependent.2.0.2" "$scriptPath\bin\release\FabricObserverWeb\win-x64\framework-dependent\FabricObserverWebApiType"
}
finally {
    Pop-Location
}