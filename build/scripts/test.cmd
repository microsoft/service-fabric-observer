cd /D "%~dp0"

vstest.console.exe FabricObserverTests\bin\Release\FabricObserverTests.dll --blame /logger:trx
