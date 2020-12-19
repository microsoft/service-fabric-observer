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

    Build-SFPkg "FabricObserverWeb.Linux.SelfContained" "$scriptPath\bin\release\FabricObserverWeb\linux-x64\self-contained\FabricObserverWebApiType"
    Build-SFPkg "FabricObserverWeb.Linux.FrameworkDependent" "$scriptPath\bin\release\FabricObserverWeb\linux-x64\framework-dependent\FabricObserverWebApiType"

    Build-SFPkg "FabricObserverWeb.Windows.SelfContained" "$scriptPath\bin\release\FabricObserverWeb\win-x64\self-contained\FabricObserverWebApiType"
    Build-SFPkg "FabricObserverWeb.Windows.FrameworkDependent" "$scriptPath\bin\release\FabricObserverWeb\win-x64\framework-dependent\FabricObserverWebApiType"
}
finally {
    Pop-Location
}