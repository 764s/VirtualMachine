@echo off
setlocal enabledelayedexpansion

:: ============================================================
::  FFVM Cross-Language Benchmark Runner (Windows)
::  Runs B01-B05 in C#, FFVM, Node.js, Lua, Python
::  Output: benchmarks\cross_lang_results.md (overwrites)
::
::  Usage: benchmarks\run-cross-lang.cmd
:: ============================================================

for %%D in ("%~dp0..") do set "ROOT=%%~fD"
cd /d "%ROOT%"

set "BENCH_DIR=%ROOT%\benchmarks"
set "RAW_DIR=%TEMP%\ffvm_xlang"
set "OUTPUT=%BENCH_DIR%\cross_lang_results.md"

if not exist "%RAW_DIR%" mkdir "%RAW_DIR%"

echo [*] FFVM Cross-Language Benchmark Suite
echo     %date% %time%
echo.

set "PASS=0"
set "SKIP=0"

:: -- 1. FFVM + C# benchmarks --------------------------------
echo [*] Running FFVM + C# benchmarks...
dotnet run --project StandaloneRunner\StandaloneRunner.csproj -c Release --artifacts-path "%TEMP%\ffvm_bench_build" -- --bench > "%RAW_DIR%\ffvm.txt" 2>&1
if errorlevel 1 (
    echo     [ERROR] Build/run failed!
    type "%RAW_DIR%\ffvm.txt"
    exit /b 1
)
set /a PASS+=2
echo     Done.

:: -- 2. Node.js ----------------------------------------------
echo [*] Running Node.js benchmarks...
where node >nul 2>&1
if errorlevel 1 (
    echo     SKIPPED ^(node not found^)
    echo [XLANG_SKIP] js> "%RAW_DIR%\js.txt"
    set /a SKIP+=1
) else (
    node "%BENCH_DIR%\js\bench.js" > "%RAW_DIR%\js.txt" 2>&1
    set /a PASS+=1
    echo     Done.
)

:: -- 3. Lua --------------------------------------------------
echo [*] Running Lua benchmarks...
set "LUA_CMD="
where lua >nul 2>&1 && set "LUA_CMD=lua"
if not defined LUA_CMD (
    echo     SKIPPED ^(lua not found^)
    echo [XLANG_SKIP] lua> "%RAW_DIR%\lua.txt"
    set /a SKIP+=1
) else (
    !LUA_CMD! "%BENCH_DIR%\lua\bench.lua" > "%RAW_DIR%\lua.txt" 2>&1
    set /a PASS+=1
    echo     Done.
)

:: -- 4. Python -----------------------------------------------
echo [*] Running Python benchmarks...
set "PY_CMD="
where python >nul 2>&1 && set "PY_CMD=python"
if not defined PY_CMD (
    where py >nul 2>&1 && set "PY_CMD=py"
)
if not defined PY_CMD (
    echo     SKIPPED ^(python not found^)
    echo [XLANG_SKIP] python> "%RAW_DIR%\python.txt"
    set /a SKIP+=1
) else (
    !PY_CMD! "%BENCH_DIR%\python\bench.py" > "%RAW_DIR%\python.txt" 2>&1
    set /a PASS+=1
    echo     Done.
)

:: -- 5. Generate Report --------------------------------------
echo.
echo [*] Generating cross-language report...
powershell -ExecutionPolicy Bypass -File "%BENCH_DIR%\Generate-CrossLang.ps1" -RawDir "%RAW_DIR%" -Output "%OUTPUT%"
if errorlevel 1 (
    echo     [ERROR] Report generation failed!
    exit /b 1
)

echo.
echo [*] Results: !PASS! languages, !SKIP! skipped
echo [*] Report: %OUTPUT%

endlocal
