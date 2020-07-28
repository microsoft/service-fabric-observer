Your Observer plugins live here.

How to implement an observer in our extensibility model*

*Note that for now - while we are still developing FO 3.0 here in the develop branch - 
you can build the nupkgs by running the build and package ps1 files first:

Navigate to top level directory (where the SLN lives, for example) then,

1. ./Build-FabricObserver.ps1
2. ./Build-NugetPackages.ps1
3. Create a new .NET Core 3.1 library project, reference the nupkg you want 
	- Framework-dependent  = .NET Core 3.1 is already installed on target server
	- Self-contained = includes all the files necessary for running .NET Core 3.1 applications
4. Write an observer plugin!
5. Build your observer project, drop the output dll into the Data/Plugins folder in FabricObserver/PackageRoot.
6. Add a new config section for your observer in FabricObserver/PackageRoot/Config/Settings.xml (see example at bottom of that file)
7. Ship it! (Well, test it first =)