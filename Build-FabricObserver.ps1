
param(
	[string] $RuntimeId = "win-x64",
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"

[string] $winArmSFPackageRefOverride = "/p:Version_SFServices=7.0.1816"
[string] $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

# For SF 11/12 arm64 builds, today we need to override the SF package reference version to match the current version of the SDK 
# to ensure ARM64 x64 emulation works correctly.
if ($RuntimeId -eq "win-arm64") 
{
    $winArmSFPackageRefOverride = "/p:Version_SFServices=8.0.2707"
}

try 
{
    Push-Location $scriptPath

    Remove-Item $scriptPath\bin\release\FabricObserver\$RuntimeId -Recurse -Force -EA SilentlyContinue

    dotnet publish FabricObserver\FabricObserver.csproj $winArmSFPackageRefOverride -o bin\release\FabricObserver\$RuntimeId\self-contained\FabricObserverType\FabricObserverPkg\Code -c $Configuration -r $RuntimeId --self-contained true
    dotnet publish FabricObserver\FabricObserver.csproj $winArmSFPackageRefOverride -o bin\release\FabricObserver\$RuntimeId\framework-dependent\FabricObserverType\FabricObserverPkg\Code -c $Configuration -r $RuntimeId --self-contained false
    
    Copy-Item FabricObserver\PackageRoot\* bin\release\FabricObserver\$RuntimeId\self-contained\FabricObserverType\FabricObserverPkg\ -Recurse
    Copy-Item FabricObserver\PackageRoot\* bin\release\FabricObserver\$RuntimeId\framework-dependent\FabricObserverType\FabricObserverPkg\ -Recurse

    # ApplicationManifest - All
    Copy-Item FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricObserver\$RuntimeId\self-contained\FabricObserverType\ApplicationManifest.xml
    Copy-Item FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.xml bin\release\FabricObserver\$RuntimeId\framework-dependent\FabricObserverType\ApplicationManifest.xml
   
    # ServiceManifest - Linux
    if ($RuntimeId -eq "linux-x64") 
    {
        Copy-Item FabricObserver\PackageRoot\ServiceManifest_linux.xml bin\release\FabricObserver\$RuntimeId\framework-dependent\FabricObserverType\FabricObserverPkg\ServiceManifest.xml -Force -Confirm:$False
        Copy-Item FabricObserver\PackageRoot\ServiceManifest_linux.xml bin\release\FabricObserver\$RuntimeId\self-contained\FabricObserverType\FabricObserverPkg\ServiceManifest.xml -Force -Confirm:$False

        # Modify ApplicationManifest.xml for Linux.
    
        # Load the XML file
        [xml]$xml = Get-Content -Path "$scriptPath\bin\release\FabricObserver\linux-x64\self-contained\FabricObserverType\ApplicationManifest.xml"
        
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
            $xml.Save("$scriptPath\bin\release\FabricObserver\linux-x64\framework-dependent\FabricObserverType\ApplicationManifest.xml")
            $xml.Save("$scriptPath\bin\release\FabricObserver\linux-x64\self-contained\FabricObserverType\ApplicationManifest.xml")

            Write-Host "RunAsPolicy node inserted successfully."
        } 
        else 
        {
            Write-Host "Policies node not found."
        }
    }

    # Get rid of ServiceManifest_linux.xml from build output for all runtime targets.
    Remove-Item bin\release\FabricObserver\$RuntimeId\framework-dependent\FabricObserverType\FabricObserverPkg\ServiceManifest_linux.xml -Force -Confirm:$False
    Remove-Item bin\release\FabricObserver\$RuntimeId\self-contained\FabricObserverType\FabricObserverPkg\ServiceManifest_linux.xml -Force -Confirm:$False
}
finally 
{
    Pop-Location
}