Your Observer plugins must live in this folder. 

-- How to implement an observer with our extensibility model --

Note that the observer API lives in its own library, FabricObserver.Extensibility.dll. FO also uses this library for its internal observer impls.

1. Create a new .NET 6 Library project.

2. Install the same version of the Microsoft.ServiceFabricApps.FabricObserver.Extensibility nupkg from https://www.nuget.org/profiles/ServiceFabricApps as the version of FabricObserver you are deploying. 
   E.g., 3.2.11 if you are going to deploy FO 3.2.11.
   NOTE: You can also consume the entire FabricObserver 3.2.11 nupkg to build your plugin. Please see the SampleObserverPlugin project's csproj file for more information.

3. Write an observer!

    E.g., create a new class file, MyObserver.cs.

    This is the required signature for your observer's constructor:
   
    // FO will provide (and manage) both the FabricClient instance and StatelessServiceContext instance during startup.
    public MyObserver(FabricClient fabricClient, StatelessServiceContext context) : base(fabricClient, context)
    {
    }

    You must implement ObserverBase's two abstract functions:

    public override Task ObserveAsync()
    {
    }

    public override Task ReportAsync()
    {
    }

4. Create a [ObserverTypeName]Startup.cs file with this format (e.g., MyObserver is the name of your plugin class.):
    
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

5. Build your observer project, drop the output dll and *ALL* of its dependencies, both managed and native (this is *very* important), into the Config/Data/Plugins folder in FabricObserver/PackageRoot. 
   You can place your plugin dll and all of its dependencies in its own (*same*) folder under the Plugins directory (useful if you have multiple plugins). 
   Again, ALL plugin dll dependencies (and their dependencies, if any) need to live in the *same* folder as the plugin dll.

6. Add a new config section for your observer in FabricObserver/PackageRoot/Config/Settings.xml (see example at bottom of that file).
   Update ApplicationManifest.xml with App Parameters if you want to support Versionless Parameter-only Application Upgrades for your plugin. If not, then just use Settings.xml to host your setting values.
   Look at both FabricObserver/PackageRoot/Config/Settings.xml and FabricObserverApp/ApplicationPackageRoot/ApplicationManifest.xml for several examples of how to do this.

7. Ship it! (Well, test it first =)

If you want to build your own nupkg from FO source, then:

Open a PowerShell console, navigate to the top level directory of the FO repo (in this example, C:\Users\me\source\repos\service-fabric-observer):

cd C:\Users\me\source\repos\service-fabric-observer
./Build-FabricObserver
./Build-NugetPackages

The output from the above commands contains FabricObserver platform-specific nupkgs and a package you have to use for plugin authoring named Microsoft.ServiceFabricApps.FabricObserver.Extensibility.3.2.11.nupkg. Nupkg files from above command would be located in 
C:\Users\me\source\repos\service-fabric-observer\bin\release\FabricObserver\Nugets.