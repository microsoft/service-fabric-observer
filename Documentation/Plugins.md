## How to implement an observer plugin using FO's extensibility model


**FabricObserver version 3.1.0 introduces a refactored plugin implementation that will break existing plugins. The changes required by plugin authors are trivial, however. Please see the [SampleObserver project](/SampleObserverPlugin) for a complete sample observer plugin implementation with code comments and readme with examples of the new format.**

This document is a simple overview of how to get started with building an observer plugin. Also, for a more advanced sample, please see [ContainerObserver](https://github.com/gittorre/containerobserver).

Note: The plugin model depends on the following packages, which **must have the same versions in both your plugin project and FabricObserver**:

Current: 

**Microsoft.Extensions.DependencyInjection, Version 5.0.1**  
**Microsoft.Extensions.DependencyInjection.Abstractions, Version 5.0.0**  (Observer plugins must employ this package, which must be the same version as FabricObserver's referenced package)  

#### Steps 

FabricObserver is a .NET Core 3.1 application. A FabricObserver plugin is a .NET Standard 2.0 library that consumes FabricObserver's public API, which is housed inside a .NET Standard 2.0 library, FabricObserver.Extensibility.dll. 
Your plugin must be built as a .NET Standard 2.0 library.

Install [.Net Core 3.1](https://dotnet.microsoft.com/download/dotnet-core/3.1).

Create a new .NET Standard 2.0 library project, install the nupkg you need for your target OS (Linux (Ubuntu) or Windows):  

You can find the Microsoft-signed packages in the nuget.org gallery [here](https://www.nuget.org/profiles/ServiceFabricApps) or just run this in the package manager console:

```
Install-Package Microsoft.ServiceFabricApps.FabricObserver.Windows.SelfContained -Version 3.1.12   

or for Linux:

Install-Package Microsoft.ServiceFabricApps.FabricObserver.Linux.SelfContained -Version 3.1.12
```

Note:

FrameworkDependent = Requires that .NET Core 3.1 is already installed on target machine.  

SelfContained = Includes all the binaries necessary for running .NET Core 3.1 applications on target machine. ***This is what you will want to use for your Azure deployments.***

- Write your observer plugin!

- Build your observer project, drop the output dll into the Data/Plugins folder in FabricObserver/PackageRoot.

- Add a new config section for your observer in FabricObserver/PackageRoot/Config/Settings.xml (see example at bottom of that file)
   Update ApplicationManifest.xml with Parameters if you want to support Application Parameter Updates for your plugin.
   (Look at both FabricObserver/PackageRoot/Config/Settings.xml and FabricObserverApp/ApplicationPackageRoot/ApplicationManifest.xml for several examples of how to do this.)

- Deploy FabricObserver to your cluster. Your new observer will be managed and run just like any other observer.

#### Due to the complexity of unloading plugins at runtime, in order to add or update a plugin, you must redeploy FabricObserver. The problem is easier to solve for new plugins, as this could be done via a Data configuration update, but we have not added support for this yet.
