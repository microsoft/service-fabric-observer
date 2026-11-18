Sample observer plugin implementation. Please read/experiment with this project to learn how to build an observer plugin.
The sample targets .NET 10. Extensibility also provides .NET 8 assets for existing plugins. `CopyLocalLockFileAssemblies` copies plugin dependencies using the resolved NuGet package paths, including Polly's dependencies.

In general, there are two key pieces to this project, which will be required for your observer plugin:

Like all observers, yours must implement IObserver, which is already mostly taken care of in the
abstract base class, ObserverBase. Your observer must implement ObserverBase's abstract functions ObserveAsync and ReportAsync.

Example:

Looking at the [SampleNewObserver impl](/SampleObserverPlugin/SampleNewObserver.cs), you see a few usings:

FabricObserver.Observers.Utilities - gives you access to a large number of related utilities.
using FabricObserver.Observers.Utilities.Telemetry - gives you access to logging, telemetry and EventSource tracing capabilities.
Whatever telemetry provider you enable in the main project (FabricObserver), is what will be used here, since this plugin will be loaded into 
the FabricObserver process. 

The core idea is that writing an observer plugin is an equivalent experience to writing one inside the FabricObserver project itself.

``` C#

using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;

namespace FabricObserver.Observers
{
    public class SampleNewObserver : ObserverBase
    {
        // FabricObserver will inject the FabricClient and StatelessServiceContext instances at runtime.        
        public SampleNewObserver(FabricClient fabricClient, StatelessServiceContext context)
          : base(fabricClient, context)
        {
            //... Your impl.
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            //... Your impl.
        }

        public override async Task ReportAsync(CancellationToken token)
        {
            //... Your impl.
        }
    }
 }
```

When you reference the FabricObserver.Extensibility nuget package, you will have access to all of the public code that native FabricObserver observers consume. That is, you will have the same capabilities 
that all other observers have. The world is your oyster when it comes to creating your custom observer to do whatever the underlying platform affords. 

### Deployment prerequisites

Windows requires Service Fabric 11.5 or later. Use the self-contained application package when .NET 10 is not installed on every node; framework-dependent application packages require that runtime. A .NET 8 plugin does not require a separate .NET 8 runtime when loaded into the .NET 10 observer host.

As you can see in this project, there are two key files:

1. Your observer implementation.
2. The IFabricObserverStartup implementation.

For 2., it's designed to be a trivial - and required - implementation:

``` C#
using Microsoft.Extensions.DependencyInjection;
using FabricObserver.Observers;
using System.Fabric;

[assembly: FabricObserver.FabricObserverStartup(typeof(SampleNewObserverStartup))]
namespace FabricObserver.Observers
{
    public class SampleNewObserverStartup : IFabricObserverStartup
    {
        // FabricObserver will inject the FabricClient and StatelessServiceContext instances at runtime.
        public void ConfigureServices(IServiceCollection services, FabricClient fabricClient, StatelessServiceContext context)
        {
            services.AddScoped(typeof(ObserverBase), s => new SampleNewObserver(fabricClient, context));
        }
    }
}
```  
  
**NOTE for FO 3.2.x.x: If you are using ObserverHealthReporter in your current plugin, you will need to modify the ctor and change one parameter in the HealthReport type:**
``` C#
var healthReporter = new ObserverHealthReporter(ObserverLogger);
var healthReport = new HealthReport
{
    Code = FOErrorWarningCodes.Ok,
    HealthMessage = message.ToString(),
    NodeName = NodeName,
    Observer = ObserverName,
    Property = "SomeUniquePropertyForMyHealthEvent",
    EntityType = EntityType.Node, // this is an FO 3.2.3.831 required change.
    //ReportType = HealthReportType.Node, // this is gone in FO 3.2.3.831.
    State = HealthState.Ok
};

healthReporter.ReportHealthToServiceFabric(healthReport);

```

When you build your plugin targeting .NET 8 or .NET 10, copy the dll file and its dependencies into the Data/Plugins folder inside your build output directory. E.g., YourObserverPlugin\bin\release\net10.0\win-x64. When using the full application package, this directory contains what is effectively a decompressed sfpkg file:
```
[sourcedir]\SAMPLEOBSERVERPLUGIN\BIN\RELEASE\NET10.0\WIN-X64
│   ApplicationManifest.xml  
|   SampleNewObserver.dll  
|   SampleNewObserver.pdb  
|   SampleNewObserver.deps.json  
│  
└───FabricObserverPkg  
        Code  
        Config  
        Data  
        ServiceManifest.xml        
```
Update Config/Settings.xml with your new observer config settings. You will see a commented out example of one for the SampleNewObserver plugin. Just follow that pattern and add any specific configuration parameters for your observer. If you want to configure your observer via an Application Parameter update after it's been deployed, you will need to add the override parameters to FabricObserverApp/ApplicationPackageRoot/ApplicationManifest.xml. You will see several examples of how to do this in that
file. 

If you consume the full FabricObserver nupkg (versus just the FabricObserver.Extensibility library nupkg), you can deploy FabricObserver using the contents of your plugin project's build output directory - just remove the pdb, json, dll files from the top level directory, so it looks like this:
```
[sourcedir]\SAMPLEOBSERVERPLUGIN\BIN\RELEASE\NET10.0\WIN-X64
│   ApplicationManifest.xml  
│  
└───FabricObserverPkg  
        Code  
        Config  
        Data  
        ServiceManifest.xml        
```

### Deploy FO from your plugin build folder (assuming you employed the full FabricObserver nupkg and, in this case, btarget Windows - the target can be Windows or Linux, of course): 

* Open an Admin Powershell console.
* Connect to your cluster.
* Set a $path variable to your deployment content
* Copy bits to server
* Register application type
* Create a new instance of the fabric:/FabricObserver application
* Create a new instance of the fabric:/FabricObserver/FabricObserverService service
```Powershell
$path = "[sourcedir]\MyObserverPlugin\bin\release\net10.0\[target os platform, e.g., win-x64 or linux-x64]"
Copy-ServiceFabricApplicationPackage -ApplicationPackagePath $path -CompressPackage -ApplicationPackagePathInImageStore FabricObserverV340 -TimeoutSec 1800
Register-ServiceFabricApplicationType -ApplicationPathInImageStore FabricObserverV340
New-ServiceFabricApplication -ApplicationName fabric:/FabricObserver -ApplicationTypeName FabricObserverType -ApplicationTypeVersion 3.4.0
New-ServiceFabricService -Stateless -PartitionSchemeSingleton -ApplicationName fabric:/FabricObserver -ServiceName fabric:/FabricObserver/FabricObserverService -ServiceTypeName FabricObserverType -InstanceCount -1
```  


### What about adding nuget packages to plugins that are not also installed in FO? 

Great question. The easiest way to solve this problem is to simply put the compile time assemblies of the nuget package 
you installed into your plugin project into FO's Plugins folder along with your plugin dll. Optionally, you could copy the plugin's
referenced dlls from the nuget package into FO's code package (Code folder). 

You can see an example of this in the SampleNewObserver project: there is a nuget package installed in the plugin project that is 
not also used by FO (Polly). CopyLocalLockFileAssemblies copies Polly.dll and its runtime dependencies from the resolved NuGet package locations
to the build output folder along with the plugin dll and its pdb. Copy the plugin dll, pdb and all dependency assemblies to the Plugins folder
directly. Now, when you deploy FO from the output directory, all of the necessary libs are in place and your plugin will not fail with "file not found" exceptions from the loader.
