set PATH=%PATH%;C:\Program Files (x86)\Microsoft Visual Studio\2017\Enterprise\MSBuild\ReadyRoll\OctoPack\tools
call "C:\Program Files (x86)\Microsoft Visual Studio\2017\Enterprise\Common7\Tools\VsDevCmd.bat" -arch=amd64 -host_arch=amd64 -winsdk=10.0.16299.0 -app_platform=Desktop

cd /D "%~dp0"
cd ../../
msbuild FabricObserver.sln /p:Configuration=Release /p:Platform=x64 /property:AppInsightsKey="c065641b-ec84-43fe-a8e7-c2bcbb697995"
