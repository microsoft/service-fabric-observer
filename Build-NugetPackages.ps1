param(
    [switch] $SkipExtensibilityBuild
)

$ErrorActionPreference = "Stop"

function Install-Nuget {
    $installed = Get-Command nuget.exe -ErrorAction SilentlyContinue
    if ($installed) { return $installed.Source }
    # Path to Latest nuget.exe on nuget.org
    $source = "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe"

    # Save file to top level directory in repo
    $destination = "$scriptPath\nuget.exe"

    if (-Not [System.IO.File]::Exists($destination)) {
        #Download the file
        Invoke-WebRequest -Uri $source -OutFile $destination
    }
    return $destination
}

function Build-Nuget {
    param (
        [string]
        $packageId,

        [string]
        $basePath,

        [string] 
        $nugetSpecTemplateName
    )

    [string] $nugetSpecTemplate = [System.IO.File]::ReadAllText([System.IO.Path]::Combine($scriptPath, $nugetSpecTemplateName))

    [string] $nugetSpecPath = "$scriptPath\bin\release\FabricObserver\$($packageId).nuspec"

    [System.IO.File]::WriteAllText($nugetSpecPath,  $nugetSpecTemplate.Replace("%PACKAGE_ID%", $packageId).Replace("%ROOT_PATH%", $scriptPath))

    & $nugetPath pack $nugetSpecPath -basepath $basePath -OutputDirectory bin\release\FabricObserver\Nugets -properties NoWarn=NU5100
    if ($LASTEXITCODE -ne 0) { throw "NuGet pack failed for $packageId." }
}

[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

try {

    Push-Location $scriptPath

    if (-not $SkipExtensibilityBuild) {
        & .\Build-FOExtensibility.ps1
    }
    $nugetPath = Install-Nuget

    Build-Nuget "Microsoft.ServiceFabricApps.FabricObserver.Linux.SelfContained" "$scriptPath\bin\release\FabricObserver\linux-x64\self-contained\FabricObserverType" "FabricObserver.nuspec.template"
    Build-Nuget "Microsoft.ServiceFabricApps.FabricObserver.Linux.FrameworkDependent" "$scriptPath\bin\release\FabricObserver\linux-x64\framework-dependent\FabricObserverType" "FabricObserver.nuspec.template"

    Build-Nuget "Microsoft.ServiceFabricApps.FabricObserver.Windows.SelfContained" "$scriptPath\bin\release\FabricObserver\win-x64\self-contained\FabricObserverType" "FabricObserver.nuspec.template"
    Build-Nuget "Microsoft.ServiceFabricApps.FabricObserver.Windows.FrameworkDependent" "$scriptPath\bin\release\FabricObserver\win-x64\framework-dependent\FabricObserverType" "FabricObserver.nuspec.template"

    Build-Nuget "Microsoft.ServiceFabricApps.FabricObserver.WindowsArm64.SelfContained" "$scriptPath\bin\release\FabricObserver\win-arm64\self-contained\FabricObserverType" "FabricObserver.nuspec.template"
    Build-Nuget "Microsoft.ServiceFabricApps.FabricObserver.WindowsArm64.FrameworkDependent" "$scriptPath\bin\release\FabricObserver\win-arm64\framework-dependent\FabricObserverType" "FabricObserver.nuspec.template"
    
    # Shared-library assets are published separately for both plugin target frameworks.
    Build-Nuget "Microsoft.ServiceFabricApps.FabricObserver.Extensibility" "$scriptPath\bin\release\FabricObserver\win-x64\self-contained\FabricObserverType" "FabricObserver.Extensibility.nuspec.template"
}
finally {

    Pop-Location
}