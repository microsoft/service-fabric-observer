function Install-Nuget {
    # Path to Latest nuget.exe on nuget.org
    $source = "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe"

    # Save file to top level directory in repo
    $destination = "$scriptPath\nuget.exe"

    if (-Not [System.IO.File]::Exists($destination)) {
        #Download the file
        Invoke-WebRequest -Uri $source -OutFile $destination
    }
}

function Build-Nuget {
    param (
        [string]
        $packageId,

        [string]
        $basePath
    )
    
    [string] $nugetSpecTemplate = [System.IO.File]::ReadAllText([System.IO.Path]::Combine($scriptPath, "FabricObserver.Extensibility.nuspec.template"))

    [string] $nugetSpecPath = "$scriptPath\FabricObserver.Extensibility\bin\release\netstandard2.0\$($packageId).nuspec"

    [System.IO.File]::WriteAllText($nugetSpecPath, $nugetSpecTemplate.Replace("%PACKAGE_ID%", $packageId).Replace("%ROOT_PATH%", $scriptPath))

    .\nuget.exe pack $nugetSpecPath -basepath $basePath -OutputDirectory bin\release\FabricObserver.Extensibility\Nugets -properties NoWarn=NU5100,NU5128
}

[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

try {

    Push-Location $scriptPath

    Install-Nuget

    Build-Nuget "Microsoft.ServiceFabricApps.FabricObserver.Extensibility" "$scriptPath\FabricObserver.Extensibility\bin\release\netstandard2.0"
}
finally {

    Pop-Location
}