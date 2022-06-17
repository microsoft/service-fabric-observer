Sample observer plugin implementation. Please read/experiment with this project to learn how to build an observer plugin.

In general, there are two key pieces to this project, which will be required for your observer plugin:

Like all observers, yours must implement IObserver, which is already mostly taken care of in the
abstract base class, ObserverBase. Your observer must implement ObserverBase's abstract functions ObserveAsync and ReportAsync.
FabricObserver will automatically detect and load your plugin when it starts up.

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

When you reference the FabricObserver nuget package, you will have access to all of the public code in FabricObserver. That is, you will have the same capabilities 
that all other observers have. The world is your oyster when it comes to creating your custom observer to do whatever the underlying platform affords. 

### Note: make sure you know if .NET 6 is installed on the target server that must also be running Service Fabric 9.0 and above. If it is not, then you must use the SelfContained package. This is very important.

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
  
**NOTE: If you are using ObserverHealthReporter in your current plugin, you will need to modify your code to account for breaking changes in 3.2.x:**
``` C#
var healthReporter = new ObserverHealthReporter(ObserverLogger);
var healthReport = new HealthReport
{
    Code = FOErrorWarningCodes.Ok,
    HealthMessage = message.ToString(),
    NodeName = NodeName,
    Observer = ObserverName,
    Property = "SomeUniquePropertyForMyHealthEvent",
    EntityType = EntityType.Node, // this is an FO 3.2.x breaking change.
    //ReportType = HealthReportType.Node, // this is gone in FO 3.2.x.
    State = HealthState.Ok
};

healthReporter.ReportHealthToServiceFabric(healthReport);
```

When you build your plugin as a .NET Standard 2.0, copy the dll file and all of its dependencies into the Data/Plugins folder inside your build output directory - if you build against the full FabricObserver nupkg.
E.g., YourObserverPlugin\bin\release\netstandard2.0\win-x64. In fact, this directory will contain what is effectively a decompressed sfpkg file - if you installed the full FO nupkg:  
```
[sourcedir]\SAMPLEOBSERVERPLUGIN\BIN\RELEASE\NETSTANDARD2.0\WIN-X64  
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

You can deploy using the contents of your build out directory - just remove the pdb, json, dll files from the top level directory, so it looks like this:
```
[sourcedir]\SAMPLEOBSERVERPLUGIN\BIN\RELEASE\NETSTANDARD2.0\WIN-X64
│   ApplicationManifest.xml  
│  
└───FabricObserverPkg  
        Code  
        Config  
        Data  
        ServiceManifest.xml        
```

#### New: Microsoft.ServiceFabricApps.FabricObserver.Extensibility nuget package
This nuget package simplifies the the project dependencies for building a FabricObserver plugin. You just need to install the package into your plugin project. There is no need to add other FO related packages in the csproj file, unlike the full FO nupkg.
If you use the Microsoft.ServiceFabricApps.FabricObserver.Extensibility nuget package to build your plugin, then the only output from the build will be your plugin library and pdb. This means you would copy your plugin dll and ALL of its dependencies into your local FabricObserver repo's PackageRoot\Data\Plugins folder, for example. Or, if you
are deploying using the SFPKG from the Microsoft Github repo (Releases section), then decompress the file (change the extension to .zip first), and copy the plugin dll and ALL of its dependencies to the FabricObserverPkg\Data\Plugins folder.

### Deploy FabricObserver with your plugin in place: 

* Open an Admin Powershell console.
* Connect to your cluster.
* Set a $path variable to your deployment content
* Copy bits to server
* Register application type
* Create new instance of FabricObserver application
* Create a new instance of the FabricObserverService service
```Powershell
$path = "[sourcedir]\MyObserverPlugin\bin\release\netstandard2.0\[target os platform, e.g., win-x64 or linux-x64]"
Copy-ServiceFabricApplicationPackage -ApplicationPackagePath $path -CompressPackage -ApplicationPackagePathInImageStore FabricObserverV321 -TimeoutSec 1800
Register-ServiceFabricApplicationType -ApplicationPathInImageStore FabricObserverV321
New-ServiceFabricApplication -ApplicationName fabric:/FabricObserver -ApplicationTypeName FabricObserverType -ApplicationTypeVersion 3.2.1
New-ServiceFabricService -Stateless -PartitionSchemeSingleton -ApplicationName fabric:/FabricObserver -ServiceName fabric:/FabricObserver/FabricObserverService -ServiceTypeName FabricObserverType -InstanceCount -1
```  

### What about adding nuget packages to plugins that are not also installed in FabricObserver? 

Great question. The most effective way to solve this problem is to simply put all of the compile time assemblies of the nuget package 
you installed into your plugin project into FO's Plugins folder along with your plugin dll. 

You can see an example of this in the SampleNewObserver project: there is a nuget package installed in the plugin project that is 
not also used by FO (Polly). Post-Build events are used to copy Polly.dll (the nuget package compile time assembly) from the base nuget packages location
to the build output folder along with the plugin dll and its pdb. So, the plugin dll, pdb and referenced nuget package assembly are copied to the Plugins folder
directly. Now, when you deploy FO from the output directory, all of the necessary libs are in place and your plugin will not fail with "file not found" exceptions from the loader.
