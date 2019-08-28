call "C:\Program Files (x86)\Microsoft Visual Studio\2017\Enterprise\Common7\Tools\VsDevCmd.bat" -test
cd /D "%~dp0"
cd ../../

call "C:\Program Files (x86)\Microsoft Visual Studio\2017\Enterprise\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe" "FabricObserverTests\bin\Release\FabricObserverTests.dll" --blame /logger:trx || exit /b 1
