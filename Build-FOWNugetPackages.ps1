function Build-Nuget {
    param (
        [string]
        $packageId,

        [string]
        $basePath
    )

    [string] $nugetSpecTemplate = [System.IO.File]::ReadAllText([System.IO.Path]::Combine($scriptPath, "FabricObserverWebApi.nuspec.template"))

    [string] $nugetSpecPath = "$scriptPath\bin\release\FabricObserverWeb\$($packageId).nuspec"

    [System.IO.File]::WriteAllText($nugetSpecPath,  $nugetSpecTemplate.Replace("%PACKAGE_ID%", $packageId))

    .\nuget.exe pack $nugetSpecPath -basepath $basePath -OutputDirectory bin\release\FabricObserverWeb\Nugets -properties NoWarn=NU5100
}

[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

try {
    Push-Location $scriptPath

    Build-Nuget "Microsoft.ServiceFabricApps.FabricObserverWeb.Linux.SelfContained" "$scriptPath\bin\release\FabricObserverWeb\linux-x64\self-contained\FabricObserverWebApiType"
    Build-Nuget "Microsoft.ServiceFabricApps.FabricObserverWeb.Linux.FrameworkDependent" "$scriptPath\bin\release\FabricObserverWeb\linux-x64\framework-dependent\FabricObserverWebApiType"

    Build-Nuget "Microsoft.ServiceFabricApps.FabricObserverWeb.Windows.SelfContained" "$scriptPath\bin\release\FabricObserverWeb\win-x64\self-contained\FabricObserverWebApiType"
    Build-Nuget "Microsoft.ServiceFabricApps.FabricObserverWeb.Windows.FrameworkDependent" "$scriptPath\bin\release\FabricObserverWeb\win-x64\framework-dependent\FabricObserverWebApiType"
}
finally {
    Pop-Location
}