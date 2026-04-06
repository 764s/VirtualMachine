@echo off
setlocal enabledelayedexpansion

:: ============================================================
::  KOF98 Practice — One-Click Console (Windows)
::
::  Usage:
::    Double-click this file, or run from command line:
::      KOF98\kof98-init.cmd
::
::  Steps:
::    1. Check .NET SDK
::    2. Generate KOF98.csproj (if missing)
::    3. Build (if needed)
::    4. Run
:: ============================================================

cd /d "%~dp0.."

echo.
echo  ========================================================
echo   KOF98 Practice  -  One-Click Console
echo  ========================================================
echo.

:: ─── Step 1: Check .NET SDK ─────────────────────────────────

dotnet --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK not found.
    echo         Please install .NET 8.0 SDK from:
    echo         https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)

for /f "tokens=*" %%V in ('dotnet --version') do set "DOTNET_VER=%%V"
echo [OK] .NET SDK: %DOTNET_VER%

:: ─── Step 2: Generate KOF98.csproj ──────────────────────────

if exist "KOF98\KOF98.csproj" (
    echo [OK] KOF98\KOF98.csproj already exists, skipping.
) else (
    copy /y "KOF98\KOF98.csproj.template" "KOF98\KOF98.csproj" >nul
    if errorlevel 1 (
        echo [ERROR] Failed to copy KOF98.csproj.template
        pause
        exit /b 1
    )
    echo [OK] Generated KOF98\KOF98.csproj
)

:: ─── Step 3: Build KOF98 ────────────────────────────────────

echo.
echo [*] Building KOF98 (Release)...
dotnet build KOF98\KOF98.csproj -c Release --nologo -v q
if errorlevel 1 (
    echo.
    echo [ERROR] KOF98 build failed!
    echo.
    echo  Troubleshooting:
    echo    - Check .NET SDK version: dotnet --version
    echo    - If "net8.0 not supported", edit KOF98\KOF98.csproj
    echo      and change TargetFramework to match your SDK version.
    echo    - Ensure src\FFVM\FFVM.csproj exists (FFVM core library).
    echo.
    pause
    exit /b 1
)
echo [OK] KOF98 build succeeded.

:: ─── Step 4: Generate run-kof98.cmd ─────────────────────────

(
    echo @echo off
    echo cd /d "%%~dp0.."
    echo echo.
    echo echo  [1/2] Building KOF98 (C#^) ...
    echo echo.
    echo dotnet build KOF98\KOF98.csproj -c Release --nologo -v q
    echo if errorlevel 1 (
    echo     echo.
    echo     echo  [ERROR] C# build failed!
    echo     echo.
    echo     pause
    echo     exit /b 1
    echo ^)
    echo echo.
    echo echo  [2/2] Starting KOF98 Practice ...
    echo echo.
    echo echo  Controls: WASD/Arrows = move, J = LP, K = HP, U = LK, I = HK
    echo echo  Ctrl+C to stop
    echo echo.
    echo KOF98\bin\Release\net8.0\KOF98.exe
    echo echo.
    echo pause
) > KOF98\run-kof98.cmd
echo [OK] Generated KOF98\run-kof98.cmd

:: ─── Done ───────────────────────────────────────────────────

echo.
echo  ========================================================
echo   Initialization complete!
echo  ========================================================
echo.
echo   [92m[Generated][0m  KOF98\run-kof98.cmd
echo.
echo   Next steps:
echo     1. Double-click  [93mKOF98\run-kof98.cmd[0m  to launch the game
echo     2. Use WASD/Arrow keys to move, J/K/U/I to attack
echo     3. Ctrl+C to stop
echo.
echo   Command-line options:
echo     KOF98.exe                  Console rendering (default)
echo     KOF98.exe --headless       Simulation only (no rendering)
echo     KOF98.exe --frames 600     Run for N frames then exit
echo.

pause
endlocal
