## How to implement an observer plugin using our extensibility model*

Please see the [SampleObserver project](/SampleObserverPlugin) for a complete sample observer plugin implementation with code comments and readme. This document is a simple 
overview of how to get started with building an observer plugin.

#### Steps 

- Install .Net Core 3.1.
- Navigate to top level directory (where the SLN lives, for example) then,

For now, you can use the related nugets (target OS) available [here](https://github.com/microsoft/service-fabric-observer/releases/tag/33734835).

OR

You can build them yourself by simply running these scripts, in this order: 

- ./Build-FabricObserver.ps1
- ./Build-NugetPackages.ps1
- Create a new .NET Core 3.1 library project, reference the nupkg you want:  

	Target OS - Framework-dependent  = .NET Core 3.1 is already installed on target server  

	Target OS - Self-contained = includes all the files necessary for running .NET Core 3.1 applications

- Write an observer plugin!
- Build your observer project, drop the output dll into the Data/Plugins folder in FabricObserver/PackageRoot.
- Add a new config section for your observer in FabricObserver/PackageRoot/Config/Settings.xml (see example at bottom of that file)
   Update ApplicationManifest.xml with Parameters if you want to support Application Parameter Updates for your plugin.
   (Look at both FabricObserver/PackageRoot/Config/Settings.xml and FabricObserverApp/ApplicationPackageRoot/ApplicationManifest.xml for several examples of how to do this.)
- Deploy FabricObserver to your cluster. Your new observer will run just like any other observer.
