param(
    [string] $RuntimeId = "win-x64",
    [string] $Configuration = "release",
    [switch] $Azlinux
)

$ErrorActionPreference = "Stop"

[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition
try 
{
    Push-Location $scriptPath

    Remove-Item $scriptPath\bin\$Configuration\FabricObserver\$RuntimeId\ -Recurse -Force -EA SilentlyContinue

    dotnet publish FabricObserver\FabricObserver.csproj -o bin\$Configuration\FabricObserver\$RuntimeId\self-contained\FabricObserverType\FabricObserverPkg\Code -c $Configuration -r $RuntimeId --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "FabricObserver self-contained publish failed for $RuntimeId." }
    dotnet publish FabricObserver\FabricObserver.csproj -o bin\$Configuration\FabricObserver\$RuntimeId\framework-dependent\FabricObserverType\FabricObserverPkg\Code -c $Configuration -r $RuntimeId --self-contained false
    if ($LASTEXITCODE -ne 0) { throw "FabricObserver framework-dependent publish failed for $RuntimeId." }

    Copy-Item FabricObserver\PackageRoot\* bin\$Configuration\FabricObserver\$RuntimeId\self-contained\FabricObserverType\FabricObserverPkg\ -Recurse
    Copy-Item FabricObserver\PackageRoot\* bin\$Configuration\FabricObserver\$RuntimeId\framework-dependent\FabricObserverType\FabricObserverPkg\ -Recurse

    # ApplicationManifest - All
    Copy-Item FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\$Configuration\FabricObserver\$RuntimeId\self-contained\FabricObserverType\ApplicationManifest.xml
    Copy-Item FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\$Configuration\FabricObserver\$RuntimeId\framework-dependent\FabricObserverType\ApplicationManifest.xml

    if($RuntimeId -eq "linux-x64") 
    {
        # ServiceManifest - Linux
        Copy-Item FabricObserver\PackageRoot\ServiceManifest_linux.xml bin\$Configuration\FabricObserver\$RuntimeId\framework-dependent\FabricObserverType\FabricObserverPkg\ServiceManifest.xml -Force -Confirm:$False
        Copy-Item FabricObserver\PackageRoot\ServiceManifest_linux.xml bin\$Configuration\FabricObserver\$RuntimeId\self-contained\FabricObserverType\FabricObserverPkg\ServiceManifest.xml -Force -Confirm:$False

        if ($Azlinux)
        {
            foreach ($mode in @("self-contained", "framework-dependent"))
            {
                $manifestPath = Join-Path $scriptPath "bin\$Configuration\FabricObserver\$RuntimeId\$mode\FabricObserverType\FabricObserverPkg\ServiceManifest.xml"
                $manifestText = (Get-Content -LiteralPath $manifestPath -Raw) -replace '<Program>setcaps\.sh</Program>', '<Program>setcaps-Mariner.sh</Program>'
                Set-Content -LiteralPath $manifestPath -Value $manifestText -Encoding UTF8
            }
        }

        # Modify ApplicationManifest.xml for Linux.Add commentMore actions
    
        # Load the XML file
        [xml] $xml = Get-Content -Path "$scriptPath\bin\$Configuration\FabricObserver\linux-x64\self-contained\FabricObserverType\ApplicationManifest.xml"
        
        # Define the namespace manager
        $namespaceManager = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
        $namespaceManager.AddNamespace("fab", "http://schemas.microsoft.com/2011/01/fabric")

        # Find the Policies node using the namespace
        $policiesNode = $xml.SelectSingleNode("//fab:Policies", $namespaceManager)

        if ($policiesNode -ne $null) 
        {
            # Create the new RunAsPolicy element
            $runAsPolicy = $xml.CreateElement("RunAsPolicy", $xml.DocumentElement.NamespaceURI)
            $runAsPolicy.SetAttribute("CodePackageRef", "Code")
            $runAsPolicy.SetAttribute("UserRef", "SystemUser")
            $runAsPolicy.SetAttribute("EntryPointType", "Setup")
            
            # Import the node into the document to avoid xmlns duplication
            $importedNode = $xml.ImportNode($runAsPolicy, $true)

            # Append the new element
            $policiesNode.AppendChild($importedNode) | Out-Null

            # Save the updated AppManifest to both self-contained and framework-dependent directories.   
            $xml.Save("$scriptPath\bin\$Configuration\FabricObserver\linux-x64\framework-dependent\FabricObserverType\ApplicationManifest.xml")
            $xml.Save("$scriptPath\bin\$Configuration\FabricObserver\linux-x64\self-contained\FabricObserverType\ApplicationManifest.xml")

            Write-Host "RunAsPolicy node inserted successfully."
        } 
        else 
        {
            Write-Host "Policies node not found."
        }

        # Get rid of ServiceManifest_linux.xml from build output.
        Remove-Item bin\$Configuration\FabricObserver\$RuntimeId\framework-dependent\FabricObserverType\FabricObserverPkg\ServiceManifest_linux.xml -Force -Confirm:$False
        Remove-Item bin\$Configuration\FabricObserver\$RuntimeId\self-contained\FabricObserverType\FabricObserverPkg\ServiceManifest_linux.xml -Force -Confirm:$False
    }
}
finally 
{
    Pop-Location
}