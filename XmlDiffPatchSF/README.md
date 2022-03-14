# XmlDiffPatchSF

***There are undoubtedly bugs in this tool. Please use at your own risk and always double check the patched file.***

Simple command line utility that will diff/patch one version of a Service Fabric configuration file (ApplicationManifest.xml or Settings.xml) into another, later version, preserving setting values of the earlier version. 

This makes it trivial to update a new version of an SF base app configuration file with the current settings you have established for an earlier version, removing the need to do this manually everytime these files are updated in a new release of some application. The patched file will include all the new settings for the latest version while preserving all of the old settings even if they do not exist in the new config (this is important for [FabricObserver plugin](https://github.com/microsoft/service-fabric-observer/blob/develop/Documentation/Plugins.md) configuration, for example). Do note that this tool is not perfect. You need to review the patched file and fix any discrepencies. If you move from a very old version of FO a recent version, this is especially true. 
As mentioned above, this currently supports ApplicationManifest.xml and Settings.xml files, but can very easily be extended to support other XML files you use for SF application configuration. 

This utility employs the old-yet-still-used-by-many Microsoft XML diff/patch utility code, which in modern times is housed in a nuget library [XmlDiffPatch](https://www.nuget.org/packages/XMLDiffPatch/). 

```XmlDiffPatchSF is built as a .NET Desktop (Windows-only) console application targetting .NET Framework 4.72.```

### Usage

Help:

```XmlDiffPatchSF ? ```

Diff/Patch ApplicationManifest.xml:

```XmlDiffPatchSF "C:\repos\tools\monitoring\FO\config\ApplicationManifest-Main.xml" "C:\temp\FO-latest\FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.xml" ``` 

The above command will produce a patched file, C:\temp\FO-latest\FabricObserverApp\ApplicationPackageRoot\ApplicationManifest_patched.xml, containing any new settings for the latest version and carrying over the settings established in the earlier version, in this case named ApplicationManifest-Main.xml, which would store all of your preferred configuration settings for FO, for example (your main configuration file).

You can optionally provide a third parameter which is the full path, including file name, to the patched file - in this case, named ApplicationManifest.xml: 

```XmlDiffPatchSF "C:\repos\tools\monitoring\FO\config\ApplicationManifest-Main.xml" "C:\temp\FO-latest\FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.xml"  "C:\temp\FO-latest\FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.xml" ``` 

Note that if your current config files contain elements that the new (latest, target) file does not, then they will not be carried over. To support FabricObserver Plugins and their related configuration settings in Settings.xml and ApplicationManifest.xml, you need to pass true as the last argument:  

```XmlDiffPatchSF "C:\repos\tools\monitoring\FO\config\ApplicationManifest-Main.xml" "C:\temp\FO-latest\FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.xml" true ```

OR

```XmlDiffPatchSF "C:\repos\tools\monitoring\FO\config\ApplicationManifest-Main.xml" "C:\temp\FO-latest\FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.xml"  "C:\temp\FO-latest\FabricObserverApp\ApplicationPackageRoot\ApplicationManifest.xml" true ```

**NOTE: Make sure you run this utility over both ApplicationManifest and Settings XML files as new settings added to latest ApplicationManifest will also be present in the latest Settings.xml file.** 

Diff/Patch Settings.xml: 

```XmlDiffPatchSF "C:\repos\tools\monitoring\FO\config\Settings-Main.xml" "C:\temp\FO-latest\FabrcicObserver\PackageRoot\Config\Settings.xml" "C:\temp\FO-latest\FabrcicObserver\PackageRoot\Config\Settings.xml" ``` 

It should be easy to run this utility in a devops workflow. 
