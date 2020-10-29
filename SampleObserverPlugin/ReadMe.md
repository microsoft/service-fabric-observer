Sample observer plugin implementation. Please read/experiment with this project to learn how to build an observer plugin.

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
    public class MyObserver : ObserverBase
    {
        public SampleNewObserver()
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

When you reference the FabricObserver nuget package, you will have access to
all of the public code in FabricObserver. That is, you will have the same capabilities 
that all other observers have. The world is your oyster when it comes to creating your
custom observer to do whatever the underlying platform affords. 

### Note: make sure you know if .NET Core 3.1 is installed on the target server. If it is not, then you must use the SelfContained package. This is very important.

As you can see in this project, there are two key files:

1. Your observer implementation.
2. The IFabricObserverStartup implementation.

For 2., it's designed to be a trivial - and required - impl:

``` C#
using FabricObserver.Observers.Interfaces;
using Microsoft.Extensions.DependencyInjection;

[assembly: FabricObserver.FabricObserverStartup(typeof(FabricObserver.Observers.[Name of this class, e.g., MyObserverStartup]))]
namespace FabricObserver.Observers
{
    public class MyObserverStartup : IFabricObserverStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            _ = services.AddScoped(typeof(ObserverBase), typeof([Name of the class that holds your observer impl. E.g., MyObserver]));
        }
    }
}
```

When you build your plugin as a .NET Core 3.1 library, copy the dll file into the Data/Plugins folder inside your build output directory. E.g., YourObserverPlugin\bin\Debug\netcoreapp3.1. In fact, this directory will contain what is effectively an sfpkg file and folder structure:  
```
[sourcedir]\SAMPLEOBSERVERPLUGIN\BIN\DEBUG\NETCOREAPP3.1  
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
[sourcedir]\SAMPLEOBSERVERPLUGIN\BIN\DEBUG\NETCOREAPP3.1  
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
* Create new instance of FO, which contains your observer!
```Powershell
$path = "[sourcedir]\MyObserverPlugin\bin\debug\netcoreapp3.1"
Copy-ServiceFabricApplicationPackage -ApplicationPackagePath $path -CompressPackage -ApplicationPackagePathInImageStore FabricObserverV39 -TimeoutSec 1800
Register-ServiceFabricApplicationType -ApplicationPathInImageStore FabricObserverV3
New-ServiceFabricApplication -ApplicationName fabric:/FabricObserver -ApplicationTypeName FabricObserverType -ApplicationTypeVersion 3.0.9
```
