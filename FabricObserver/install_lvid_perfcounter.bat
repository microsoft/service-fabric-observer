
set szDir=%ProgramFiles%\Microsoft Service Fabric\bin\Fabric\Fabric.Code
set szPerfService=ESE
set szPerfDLL=eseperf
unlodctr %szPerfService%

reg.exe add "HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\%szPerfService%\Performance" /v "Open" /t REG_SZ /d "OpenPerformanceData" /f
reg.exe add "HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\%szPerfService%\Performance" /v "Collect" /t REG_SZ /d "CollectPerformanceData" /f
reg.exe add "HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\%szPerfService%\Performance" /v "Close" /t REG_SZ /d "ClosePerformanceData" /f
reg.exe add "HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\%szPerfService%\Performance" /v "Library" /t REG_SZ /d "%szDir%\%szPerfDLL%.dll" /f
reg.exe add "HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\%szPerfService%\Performance" /v "Show Advanced Counters" /t REG_DWORD /d 1 /f

lodctr "%szDir%\%szPerfDLL%.ini"