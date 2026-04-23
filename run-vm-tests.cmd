@echo off
setlocal

cd /d "%~dp0"
if not exist "Logs" mkdir "Logs"
set "LOG_FILE=Logs\run-vm-tests.latest.log"

echo [run-vm-tests] Running test suite...
echo [run-vm-tests] Full output: %LOG_FILE%

dotnet run --project StandaloneRunner > "%LOG_FILE%" 2>&1
set "EXIT_CODE=%ERRORLEVEL%"

echo.
echo [run-vm-tests] Last 30 lines:
powershell -NoProfile -Command "if (Test-Path '%LOG_FILE%') { Get-Content '%LOG_FILE%' -Tail 30 }"
echo.

if not "%EXIT_CODE%"=="0" (
	echo [run-vm-tests] FAILED with exit code %EXIT_CODE%
) else (
	echo [run-vm-tests] PASSED with exit code 0
)

if not defined TERM_PROGRAM (
	echo.
	echo [run-vm-tests] Press any key to close...
	pause >nul
)

endlocal
exit /b %EXIT_CODE%
