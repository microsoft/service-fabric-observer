cd /D "%~dp0"
cd ../../
msbuild FabricObserver.sln /p:Configuration=Release /p:Platform=x64 /property:AppInsightsKey="c065641b-ec84-43fe-a8e7-c2bcbb697995"
