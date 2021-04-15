[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

function Build-SFPkg {
    param (
        [string]
        $packageId,

        [string]
        $basePath
    )

    $ProgressPreference = "SilentlyContinue"

    [string] $outputDir = "$scriptPath\bin\release\ClusterObserver\SFPkgs"
    [string] $zipPath = "$outputDir\$($packageId).zip"
    [System.IO.Directory]::CreateDirectory($outputDir) | Out-Null

    Compress-Archive "$basePath\*"  $zipPath -Force

    Move-Item -Path $zipPath -Destination ($zipPath.Replace(".zip", ".sfpkg"))
}

try {
    Push-Location $scriptPath

    Build-SFPkg "Microsoft.ServiceFabricApps.ClusterObserver.Linux.SelfContained.2.1.7" "$scriptPath\bin\release\ClusterObserver\linux-x64\self-contained\ClusterObserverType"
    Build-SFPkg "Microsoft.ServiceFabricApps.ClusterObserver.Linux.FrameworkDependent.2.1.7" "$scriptPath\bin\release\ClusterObserver\linux-x64\framework-dependent\ClusterObserverType"

    Build-SFPkg "Microsoft.ServiceFabricApps.ClusterObserver.Windows.SelfContained.2.1.7" "$scriptPath\bin\release\ClusterObserver\win-x64\self-contained\ClusterObserverType"
    Build-SFPkg "Microsoft.ServiceFabricApps.ClusterObserver.Windows.FrameworkDependent.2.1.7" "$scriptPath\bin\release\ClusterObserver\win-x64\framework-dependent\ClusterObserverType"
}
finally {
    Pop-Location
}
