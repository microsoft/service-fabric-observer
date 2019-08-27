set PATH=%PATH%;C:\Program Files (x86)\Microsoft Visual Studio\2017\Enterprise\MSBuild\ReadyRoll\OctoPack\tools

call "C:\Program Files (x86)\Microsoft Visual Studio\2017\Enterprise\Common7\Tools\VsDevCmd.bat" -arch=amd64 -host_arch=amd64 -winsdk=10.0.16299.0 -app_platform=Desktop
call "C:\Program Files (x86)\Microsoft Visual Studio\2017\Enterprise\Common7\Tools\VsDevCmd.bat" -test

cd /D "%~dp0"
msbuild ..\..\FabricObserver.sln

nuget.exe restore "..\..\FabricObserver.sln"
dotnet.exe restore "..\..\FabricObserver.sln"
