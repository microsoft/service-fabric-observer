***Build Notes***

After cloning, the .NET Core 2.2 SDK will need to be manually installed in order for the ObserverWeb to be built - [.NET Core 2.2 SDK](https://dotnet.microsoft.com/download/dotnet-core/2.2). ObserverWeb is the stateless ASP.NET Core REST API service used to query FabricObserver for Info, Errors, Warning states. Currently, the only output supported is HTML. JSON forthcoming (TODO), which is what most callers will expect from an REST API.

*Note: Different versions of Visual Studio necessitate different versions of the SDK to be installed - make sure the correct version is installed.*
