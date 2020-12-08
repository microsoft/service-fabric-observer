Your Observer plugins must live here. 

You must build your plugin as a .NET Standard 2.0 library.

How to implement an observer plugin with our extensibility model. Note that the observer API surface lives in its own library (.NET Standard 2.0), FabricObserver.Extensibility.dll. FO also uses this library for its internal observer impls.

****NOTE****: If you wrote an observer with an earlier build of FO (anything up to and including version 3.0.11), then this new refactoring will break your existing implementation. It is really simple to fix in your plugin codebase:

1. Your observer ctor must be changed to take new paramaters (2) that are passed into base(). E.g.,

This is the required format for your ctor:

public MyObserver(FabricClient fabricClient, StatelessServiceContext context)
            : base(fabricClient, context)

ObserverBase now lives in the extensibility library and this required changing it's constructor. FO will take care of creating the instances of these new parameter types when it creates your plugin instance,
so you just program as you did before and everything will work as it used to.

2. In your startup class, you must change ConfigureServices to satisfy the new IFabricStartup defintion, then create an instance of MyObserver passing in the fabricClient and context parameters:

    public class MyObserverStartup : IFabricObserverStartup
    {
        public void ConfigureServices(IServiceCollection services, FabricClient fabricClient, StatelessServiceContext context)
        {
            services.AddScoped(typeof(ObserverBase), s => new MyObserver(fabricClient, context));
        }
    }


Install .Net Core 3.1.

Grab the latest FO nupkg from Releases section that suits your target OS (Linux or Windows).
In general, you will want the SelfContained package, not the FrameworkDependent (for example, Azure OS images do not ship with .NET Core 3.1 aboard,
so you need self-contained build which has all the binaries needed to run a .NET Core 3.1 app.)

If you want to build your own nupkgs from FO source, then:

Navigate to top level directory (where the SLN lives, for example), then:

1. ./Build-FabricObserver.ps1
2. ./Build-NugetPackages.ps1
3. Create a new .NET Standard (2.0) library project, install the FO nupkg you created: 
	Target OS - Framework-dependent  = .NET Core 3.1 is already installed on target server
	Target OS - Self-contained = includes all the files necessary for running .NET Core 3.1 applications
4. Write an observer plugin!
5. Build your observer project, drop the output dll into the Data/Plugins folder in FabricObserver/PackageRoot.
6. Add a new config section for your observer in FabricObserver/PackageRoot/Config/Settings.xml (see example at bottom of that file)
   Update ApplicationManifest.xml with Parameters if you want to support Application Parameter Updates for your plugin.
   (Look at both FabricObserver/PackageRoot/Config/Settings.xml and FabricObserverApp/ApplicationPackageRoot/ApplicationManifest.xml for several examples of how to do this.)
7. Ship it! (Well, test it first =)