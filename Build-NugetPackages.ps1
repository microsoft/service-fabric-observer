function Build-Nuget {
    param (
        [string]
        $packageId,

        [string]
        $basePath
    )

    [string] $nugetSpecTemplate = [System.IO.File]::ReadAllText([System.IO.Path]::Combine($scriptPath, "FabricObserver.nuspec.template"))

    [string] $nugetSpecPath = "$scriptPath\bin\release\FabricObserver\$($packageId).nuspec"

    [System.IO.File]::WriteAllText($nugetSpecPath,  $nugetSpecTemplate.Replace("%PACKAGE_ID%", $packageId))

    .\nuget.exe pack $nugetSpecPath -basepath $basePath -OutputDirectory bin\release\FabricObserver\Nugets -properties NoWarn=NU5100
}

[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

try {
    Push-Location $scriptPath

    Build-Nuget "FabricObserver.Linux.SelfContained" "$scriptPath\bin\release\FabricObserver\linux-x64\self-contained\FabricObserverType"
    Build-Nuget "FabricObserver.Linux.FrameworkDependent" "$scriptPath\bin\release\FabricObserver\linux-x64\framework-dependent\FabricObserverType"

    Build-Nuget "FabricObserver.Windows.SelfContained" "$scriptPath\bin\release\FabricObserver\win-x64\self-contained\FabricObserverType"
    Build-Nuget "FabricObserver.Windows.FrameworkDependent" "$scriptPath\bin\release\FabricObserver\win-x64\framework-dependent\FabricObserverType"
}
finally {
    Pop-Location
}