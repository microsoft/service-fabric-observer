## How to implement an observer plugin using FO's extensibility model

#### Note that starting in version 2.2.0, ClusterObserver supports the FO plugin model. So, you can build cluster-level monitoring plugins should you so desire.

1. Create a new .NET core library project. You should target net6.0 in your csproj because that is the target net SDK version that FabricObserver is built for.

2. Install the latest Microsoft.ServiceFabricApps.FabricObserver.Extensibility nupkg from https://www.nuget.org/profiles/ServiceFabricApps into your plugin project.

3. Write an observer plugin!

    E.g., create a new class file, MyObserver.cs.

    This is the required signature for your plugin's constructor: 

```C#
    // FO will provide (and manage) both the FabricClient instance and StatelessServiceContext instance during startup.
    public MyObserver(FabricClient fabricClient, StatelessServiceContext context) : base(fabricClient, context)
    {
    }
```

You must implement ObserverBase's two abstract functions: 

```C#
    public override Task ObserveAsync()
    {
    }

    public override Task ReportAsync()
    {
    }
```

4. Create a [PluginTypeName]Startup.cs file with this format (e.g., MyObserver is the name of your plugin class.):
    
```C#
    using System.Fabric;
    using FabricObserver;
    using FabricObserver.Observers;
    using Microsoft.Extensions.DependencyInjection;

    [assembly: FabricObserverStartup(typeof(MyObserverStartup))]
    namespace FabricObserver.Observers
    {
        public class MyObserverStartup : IFabricObserverStartup
        {
            public void ConfigureServices(IServiceCollection services, FabricClient fabricClient, StatelessServiceContext context)
            {
                services.AddScoped(typeof(ObserverBase), s => new MyObserver(fabricClient, context));
            }
        }
    }
```

5. Build your observer project, drop the output dll and *ALL* of its dependencies, both managed and native (this is *very* important), into the Config/Data/Plugins folder in FabricObserver/PackageRoot. 
   You can place your plugin dll and all of its dependencies in its own (*same*) folder under the Plugins directory (useful if you have multiple plugins). 
   Again, ALL plugin dll dependencies (and their dependencies, if any) need to live in the *same* folder as the plugin dll.

6. Add a new config section for your observer in FabricObserver/PackageRoot/Config/Settings.xml (see example at bottom of that file)
   Update ApplicationManifest.xml with Parameters if you want to support Versionless Application Parameter-only Upgrades for your plugin.
   (Look at both FabricObserver/PackageRoot/Config/Settings.xml and FabricObserverApp/ApplicationPackageRoot/ApplicationManifest.xml for several examples of how to do this.)

7. Ship it! (Well, test it first =)


If you want to build your own nupkg from FO source, then:

Open a PowerShell console, navigate to the top level directory of the FO repo (in this example, C:\Users\me\source\repos\service-fabric-observer):

```PowerShell
cd C:\Users\me\source\repos\service-fabric-observer
./Build-FabricObserver
./Build-NugetPackages
```
The output from the above commands, FabricObserver platform-specific nupkgs and a package you have to use for plugin authoring named Microsoft.ServiceFabricApps.FabricObserver.Extensibility.3.2.0.nupkg, would be located in 
C:\Users\me\source\repos\service-fabric-observer\bin\release\FabricObserver\Nugets.