cd /D "%~dp0"
cd ../../

vstest.console.exe FabricObserverTests\bin\Release\FabricObserverTests.dll --blame /logger:trx
