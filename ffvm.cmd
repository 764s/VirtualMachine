@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

:: ============================================================
::  FFVM - Master Command Menu
::  Usage: ffvm.cmd [command]
::
::  Commands:
::    sandbox    Build & run Sandbox (with DAP debug server)
::    test       Run VM unit tests
::    bench      Run benchmarks (results only)
::    bench-all  Run benchmarks + update performance history
::    init       First-time Sandbox initialization
::    help       Show this menu
::
::  No argument = interactive menu.
:: ============================================================

set "CMD=%~1"
set "INTERACTIVE=0"

:: Direct command mode: run once and exit
if "%CMD%"=="sandbox"   goto :do_sandbox
if "%CMD%"=="test"      goto :do_test
if "%CMD%"=="bench"     goto :do_bench
if "%CMD%"=="bench-all" goto :do_bench_all
if "%CMD%"=="init"      goto :do_init
if "%CMD%"=="help"      goto :show_help
if "%CMD%"==""          goto :menu

echo [!] Unknown command: %CMD%
echo.
goto :show_help

:: ?? Help (non-interactive) ??????????????????????????????????

:show_help
echo.
echo  ============================================================
echo   FFVM - Master Command Menu
echo  ============================================================
echo.
echo   ffvm sandbox     Build ^& run Sandbox (with DAP debug)
echo   ffvm test        Run VM unit tests (StandaloneRunner)
echo   ffvm bench       Run benchmarks (update results only)
echo   ffvm bench-all   Run benchmarks + update perf history
echo   ffvm init        First-time Sandbox initialization
echo.
goto :done

:: ?? Interactive Menu ????????????????????????????????????????

:menu
set "INTERACTIVE=1"
echo.
echo  ============================================================
echo   FFVM - Master Command Menu
echo  ============================================================
echo.
echo   [1]  sandbox     Build ^& run Sandbox (with DAP debug)
echo   [2]  test        Run VM unit tests (StandaloneRunner)
echo   [3]  bench       Run benchmarks (update results only)
echo   [4]  bench-all   Run benchmarks + update perf history
echo   [5]  init        First-time Sandbox initialization
echo   [Q]  quit
echo.
set /p "CHOICE=  Select [1-5, Q]: "

if "%CHOICE%"=="1" goto :do_sandbox
if "%CHOICE%"=="2" goto :do_test
if "%CHOICE%"=="3" goto :do_bench
if "%CHOICE%"=="4" goto :do_bench_all
if "%CHOICE%"=="5" goto :do_init
if /i "%CHOICE%"=="Q" goto :done
echo.
echo  [!] Invalid choice.
goto :menu

:: ?? Commands ????????????????????????????????????????????????

:do_sandbox
echo.
echo  [FFVM] Building ^& launching Sandbox...
echo.
dotnet build Sandbox\Sandbox.csproj -c Release --nologo -v q
if errorlevel 1 (
    echo  [ERROR] Sandbox build failed!
    goto :after_cmd
)
echo.
Sandbox\bin\Release\net10.0\Sandbox.exe --debug
goto :after_cmd

:do_test
echo.
echo  [FFVM] Running VM tests...
echo.
dotnet run --project StandaloneRunner
goto :after_cmd

:do_bench
echo.
echo  [FFVM] Running benchmarks...
echo.
call benchmarks\run-benchmarks.cmd
goto :after_cmd

:do_bench_all
echo.
echo  [FFVM] Running benchmarks + updating history...
echo.
call benchmarks\run-all.cmd
goto :after_cmd

:do_init
echo.
echo  [FFVM] Running first-time Sandbox initialization...
echo.
call Sandbox\sandbox-init.cmd
goto :after_cmd

:after_cmd
echo.
echo  ------------------------------------------------------------
if "!INTERACTIVE!"=="1" (
    echo   Press any key to return to menu...
    pause >nul
    goto :menu
)

:done
echo.
endlocal
