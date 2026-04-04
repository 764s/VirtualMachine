@echo off
setlocal enabledelayedexpansion

:: ============================================================
::  FFVM One-Click Benchmark Suite (Windows Local)
::  Runs benchmarks, updates benchmark_results.md AND
::  performance_history.md in one step.
::
::  Usage: benchmarks\run-all.cmd
::
::  This does NOT modify the existing scripts used by CI.
::  CI uses: generate-report.sh + update-history.sh (bash)
::  This uses: run-benchmarks.cmd + Update-History.ps1 (Windows)
:: ============================================================

for %%D in ("%~dp0..") do set "ROOT=%%~fD"
set "RAW=%TEMP%\ffvm_bench_raw.txt"

:: Step 1: Run benchmarks and generate benchmark_results.md
echo ============================================================
echo  Step 1/2: Running benchmarks...
echo ============================================================
call "%~dp0run-benchmarks.cmd"
if errorlevel 1 (
    echo [!] Benchmark run failed.
    exit /b 1
)

:: Step 2: Update performance_history.md
echo.
echo ============================================================
echo  Step 2/2: Updating performance history...
echo ============================================================
powershell -ExecutionPolicy Bypass -File "%~dp0Update-History.ps1" "%RAW%"
if errorlevel 1 (
    echo [!] History update failed.
    exit /b 1
)

echo.
echo ============================================================
echo  Done. Updated:
echo    - benchmarks\benchmark_results.md
echo    - benchmarks\performance_history.md
echo ============================================================

endlocal
