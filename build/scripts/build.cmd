set PATH=%PATH%;C:\Program Files (x86)\Microsoft Visual Studio\2017\Enterprise\MSBuild\ReadyRoll\OctoPack\tools
call "C:\Program Files (x86)\Microsoft Visual Studio\2017\Enterprise\Common7\Tools\VsDevCmd.bat"
cd /D "%~dp0"
cd ../../
nuget.exe restore "FabricObserver.sln"
msbuild /t:Restore "FabricObserver.sln"
msbuild /p:Configuration=Release /p:Platform=x64 /property:AppInsightsKey="c065641b-ec84-43fe-a8e7-c2bcbb697995" ".\TelemetryLib\TelemetryLib.csproj"
msbuild /p:Configuration=Release /p:Platform=x64 ".\FabricObserver\FabricObserver.csproj"
msbuild /p:Configuration=Release /p:Platform=x64 ".\FabricObserverTests\FabricObserverTests.csproj" /p:OutputPath="./"
dotnet restore ".\FabricObserverWeb\FabricObserverWeb.csproj"
dotnet build /p:Configuration=Release /p:Platform=AnyCPU ".\FabricObserverWeb\FabricObserverWeb.csproj"
