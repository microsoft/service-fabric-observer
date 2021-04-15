Sample observer plugin implementation. Please read/experiment with this project to learn how to build an observer plugin.

**NOTE: FabricObserver version 3.1.0 introduces a refactored plugin implementation that will break existing plugins. The changes required by plugin authors are trivial, however. You can see what needs to be done below. Basically,
you will need to modify your plugin's constructor to take two parameters and pass them to the base ctor. Then, in your startup class, you will need to modify the implementation to support the new IFabricStartup.ConfigureServices signature.**

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

When you reference the FabricObserver nuget package, you will have access to all of the public code in FabricObserver. That is, you will have the same capabilities 
that all other observers have. The world is your oyster when it comes to creating your custom observer to do whatever the underlying platform affords. 

### Note: make sure you know if .NET Core 3.1 is installed on the target server. If it is not, then you must use the SelfContained package. This is very important.

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
  
**NOTE for FO 3.1.0: If you are using ObserverHealthReporter in your current plugin, you will need to modify the ctor:**
``` C#
// FabricClientInstance is a built-in object in ObserverBase. 
var healthReporter = new ObserverHealthReporter(ObserverLogger, FabricClientInstance);
```

When you build your plugin as a .NET Standard 2.0, copy the dll file into the Data/Plugins folder inside your build output directory. E.g., YourObserverPlugin\bin\release\netstandard2.0\win-x64. In fact, this directory will contain what is effectively a decompressed sfpkg file:  
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

### Deploy FO from your plugin build folder (assuming you build FO on Windows - the target can be Windows or Linux, of course.): 

* Open an Admin Powershell console.
* Connect to your cluster.
* Set a $path variable to your deployment content
* Copy bits to server
* Register application type
* Create new instance of FO, which will contain your observer plugin
```Powershell
$path = "[sourcedir]\MyObserverPlugin\bin\release\netstandard2.0\[target os platform, e.g., win-x64 or linux-x64]"
Copy-ServiceFabricApplicationPackage -ApplicationPackagePath $path -CompressPackage -ApplicationPackagePathInImageStore FabricObserverV319 -TimeoutSec 1800
Register-ServiceFabricApplicationType -ApplicationPathInImageStore FabricObserverV316
New-ServiceFabricApplication -ApplicationName fabric:/FabricObserver -ApplicationTypeName FabricObserverType -ApplicationTypeVersion 3.1.9
```
