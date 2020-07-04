param (
    [switch]
    $SkipBuild
)

$ErrorActionPreference = "Stop"

function Update-SettingsXmlForLinux {
    param (
        [string] $filePath
    )

    [string[]] $lines = [System.IO.File]::ReadAllLines($filePath)
    $lines = ( $lines | ForEach-Object { $_.Replace("C:\observer_logs", "observer_logs").Replace("observer_logs\fabric_observer_data", "observer_logs/fabric_observer_data") } )
    [System.IO.File]::WriteAllLines($filePath, $lines)
}

function Update-ServiceManifestForLinux {
    param (
        [string] $filePath
    )

    [string[]] $lines = [System.IO.File]::ReadAllLines($filePath)
    $lines = ( $lines | ForEach-Object { $_.Replace("FabricObserver.exe", "FabricObserver") } )
    [System.IO.File]::WriteAllLines($filePath, $lines)
}

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

    .\nuget.exe pack $nugetSpecPath -basepath $basePath -OutputDirectory bin\release\FabricObserver -properties NoWarn=NU5100
}

$Configuration="Release"
[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

try {
    Push-Location $scriptPath

    Remove-Item $scriptPath\bin\release\FabricObserver\ -Recurse -Force -EA SilentlyContinue

    if (-not $SkipBuild) {
        dotnet publish FabricObserver\FabricObserver.csproj -o bin\release\FabricObserver\linux-x64\self-contained\FabricObserverType\FabricObserverPkg\Code -c $Configuration -r linux-x64 --self-contained true
        dotnet publish FabricObserver\FabricObserver.csproj -o bin\release\FabricObserver\linux-x64\framework-dependent\FabricObserverType\FabricObserverPkg\Code -c $Configuration -r linux-x64 --self-contained false
        dotnet publish FabricObserver\FabricObserver.csproj -o bin\release\FabricObserver\win-x64\self-contained\FabricObserverType\FabricObserverPkg\Code -c $Configuration -r win-x64 --self-contained true
        dotnet publish FabricObserver\FabricObserver.csproj -o bin\release\FabricObserver\win-x64\framework-dependent\FabricObserverType\FabricObserverPkg\Code -c $Configuration -r win-x64 --self-contained false
    }

    Copy-Item FabricObserver\PackageRoot\* bin\release\FabricObserver\linux-x64\self-contained\FabricObserverType\FabricObserverPkg\ -Recurse
    Copy-Item FabricObserver\PackageRoot\* bin\release\FabricObserver\linux-x64\framework-dependent\FabricObserverType\FabricObserverPkg\ -Recurse

    Copy-Item FabricObserver\PackageRoot\* bin\release\FabricObserver\win-x64\self-contained\FabricObserverType\FabricObserverPkg\ -Recurse
    Copy-Item FabricObserver\PackageRoot\* bin\release\FabricObserver\win-x64\framework-dependent\FabricObserverType\FabricObserverPkg\ -Recurse


    Copy-Item FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.Linux.xml bin\release\FabricObserver\linux-x64\self-contained\FabricObserverType\ApplicationManifest.xml
    Copy-Item FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.Linux.xml bin\release\FabricObserver\linux-x64\framework-dependent\FabricObserverType\ApplicationManifest.xml

    Copy-Item FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricObserver\win-x64\self-contained\FabricObserverType\ApplicationManifest.xml
    Copy-Item FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricObserver\win-x64\framework-dependent\FabricObserverType\ApplicationManifest.xml

    Update-SettingsXmlForLinux "$scriptPath\bin\release\FabricObserver\linux-x64\self-contained\FabricObserverType\FabricObserverPkg\Config\Settings.xml"
    Update-SettingsXmlForLinux "$scriptPath\bin\release\FabricObserver\linux-x64\framework-dependent\FabricObserverType\FabricObserverPkg\Config\Settings.xml"

    Update-ServiceManifestForLinux "$scriptPath\bin\release\FabricObserver\linux-x64\self-contained\FabricObserverType\FabricObserverPkg\ServiceManifest.xml"
    Update-ServiceManifestForLinux "$scriptPath\bin\release\FabricObserver\linux-x64\framework-dependent\FabricObserverType\FabricObserverPkg\ServiceManifest.xml"

    Build-Nuget "FabricObserver.Linux.SelfContained" "$scriptPath\bin\release\FabricObserver\linux-x64\self-contained\FabricObserverType"
    Build-Nuget "FabricObserver.Linux.FrameworkDependent" "$scriptPath\bin\release\FabricObserver\linux-x64\framework-dependent\FabricObserverType"

    Build-Nuget "FabricObserver.Windows.SelfContained" "$scriptPath\bin\release\FabricObserver\win-x64\self-contained\FabricObserverType"
    Build-Nuget "FabricObserver.Windows.FrameworkDependent" "$scriptPath\bin\release\FabricObserver\win-x64\framework-dependent\FabricObserverType"
}
finally {
    Pop-Location
}