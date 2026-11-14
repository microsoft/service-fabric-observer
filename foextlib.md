## FabricObserver Extensibility Library 3.4.0 (.NET 8 and .NET 10)

FabricObserver.Extensibility provides .NET 8 and .NET 10 assets for building custom observers that extend FabricObserver's and ClusterObserver's capabilities. A custom observer is managed just like a built-in observer.

FabricObserver 3.4.0 and ClusterObserver 2.4.0 run on .NET 10. Windows deployment requires Service Fabric 11.5 or later. Existing .NET 8 plugins using the documented interfaces remain supported; the shared contract assembly identity is preserved.

### How to implement an observer using FO's extensibility model

1. Create a library project targeting net10.0, or net8.0 when preserving a .NET 8 plugin.

2. Install the latest Microsoft.ServiceFabricApps.FabricObserver.Extensibility nupkg from https://www.nuget.org/profiles/ServiceFabricApps into your plugin project.

3. Write an observer plugin!

### Steps

- Create a new class file, MyObserver.cs.

    This is the required signature for your plugin's constructor: 

```C#
    // FO will provide (and manage) both the FabricClient instance and StatelessServiceContext instance during startup.
    public MyObserver(FabricClient fabricClient, StatelessServiceContext context) : base(fabricClient, context)
    {
    }
```

- Implement ObserverBase's two abstract functions: 

```C#
    public override Task ObserveAsync(CancellationToken token)
    {
    }

    public override Task ReportAsync(CancellationToken token)
    {
    }
```

- Create a [PluginTypeName]Startup.cs file
    
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

- Build, put the output dll and *ALL* of its dependencies, both managed and native (this is *very* important), into the Data/Plugins folder in FabricObserver/PackageRoot.
  You can place your plugin dll and all of its dependencies in its own folder under the Plugins directory (useful if you have multiple plugins). 
  Again, ALL plugin dll dependencies (and their dependencies, if any) need to live in the *same* folder as the plugin dll. Note that if FabricObserver already employs the same version of dependency dll,
  then you can omit adding the dependency manually.

- Add a new config section for your observer in FabricObserver/PackageRoot/Config/Settings.xml (see example at bottom of that file)
  Update ApplicationManifest.xml with Parameters if you want to support Versionless Application Parameter-only Upgrades for your plugin.

- Ship it! (Well, test it first =)