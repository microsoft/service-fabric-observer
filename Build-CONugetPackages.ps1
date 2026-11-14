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
        $basePath
    )

    [string] $nugetSpecTemplate = [System.IO.File]::ReadAllText([System.IO.Path]::Combine($scriptPath, "ClusterObserver.nuspec.template"))

    [string] $nugetSpecPath = "$scriptPath\bin\release\ClusterObserver\$($packageId).nuspec"

     [System.IO.File]::WriteAllText($nugetSpecPath,  $nugetSpecTemplate.Replace("%PACKAGE_ID%", $packageId).Replace("%ROOT_PATH%", $scriptPath))

    & $nugetPath pack $nugetSpecPath -basepath $basePath -OutputDirectory bin\release\ClusterObserver\Nugets -properties NoWarn=NU5100
    if ($LASTEXITCODE -ne 0) { throw "NuGet pack failed for $packageId." }
}

[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

try {

    Push-Location $scriptPath

    & .\Build-FOExtensibility.ps1
    $nugetPath = Install-Nuget

    Build-Nuget "Microsoft.ServiceFabricApps.ClusterObserver.Linux.SelfContained" "$scriptPath\bin\release\ClusterObserver\linux-x64\self-contained\ClusterObserverType"
    Build-Nuget "Microsoft.ServiceFabricApps.ClusterObserver.Linux.FrameworkDependent" "$scriptPath\bin\release\ClusterObserver\linux-x64\framework-dependent\ClusterObserverType"

    Build-Nuget "Microsoft.ServiceFabricApps.ClusterObserver.Windows.SelfContained" "$scriptPath\bin\release\ClusterObserver\win-x64\self-contained\ClusterObserverType"
    Build-Nuget "Microsoft.ServiceFabricApps.ClusterObserver.Windows.FrameworkDependent" "$scriptPath\bin\release\ClusterObserver\win-x64\framework-dependent\ClusterObserverType"

    Build-Nuget "Microsoft.ServiceFabricApps.ClusterObserver.WindowsArm64.SelfContained" "$scriptPath\bin\release\ClusterObserver\win-arm64\self-contained\ClusterObserverType"
    Build-Nuget "Microsoft.ServiceFabricApps.ClusterObserver.WindowsArm64.FrameworkDependent" "$scriptPath\bin\release\ClusterObserver\win-arm64\framework-dependent\ClusterObserverType"
}
finally {

    Pop-Location
}
