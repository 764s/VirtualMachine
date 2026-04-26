@echo off
setlocal

:: ============================================================
::  KOF98_CS Practice -- One-Click Init (Windows)
::
::  KOF98_CS = Game layer + CS-simulation layer + Raylib view.
::  No FFVM dependency, no FFS scripts -- pure C# baseline.
::
::  Usage:
::    Double-click this file, or run from command line:
::      KOF98_CS\kof98_cs-init.cmd
::
::  Steps:
::    1. Check .NET SDK
::    2. Generate KOF98_CS.csproj (if missing)
::    3. Build KOF98.Game + KOF98.CsSim + KOF98_CS
::    4. Generate run-kof98_cs.cmd
::    5. Generate VS Code launch.json / tasks.json (C# debug only)
:: ============================================================

cd /d "%~dp0.."

echo.
echo  ========================================================
echo   KOF98_CS Practice  -  One-Click Init
echo  ========================================================
echo.

:: --- Step 1: Check .NET SDK -----------------------------------

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

:: --- Step 2: Generate KOF98_CS.csproj -------------------------

if exist "KOF98_CS\KOF98_CS.csproj" (
    echo [OK] KOF98_CS\KOF98_CS.csproj already exists, skipping.
) else (
    copy /y "KOF98_CS\KOF98_CS.csproj.template" "KOF98_CS\KOF98_CS.csproj" >nul
    if errorlevel 1 (
        echo [ERROR] Failed to copy KOF98_CS.csproj.template
        pause
        exit /b 1
    )
    echo [OK] Generated KOF98_CS\KOF98_CS.csproj
)

:: --- Step 3: Build KOF98_CS (also pulls in Game + CsSim libs) -

echo.
echo [*] Building KOF98_CS (Release)...
dotnet build KOF98_CS\KOF98_CS.csproj -c Release --nologo -v q
if errorlevel 1 (
    echo.
    echo [ERROR] KOF98_CS build failed!
    echo.
    echo  Troubleshooting:
    echo    - Check .NET SDK version: dotnet --version
    echo    - If "net8.0 not supported", edit KOF98_CS\KOF98_CS.csproj
    echo      and change TargetFramework to match your SDK version.
    echo    - Ensure KOF98.Game\KOF98.Game.csproj and
    echo      KOF98.CsSim\KOF98.CsSim.csproj exist.
    echo.
    pause
    exit /b 1
)
echo [OK] KOF98_CS build succeeded.

:: --- Step 4: Generate run-kof98_cs.cmd ------------------------

(
    echo @echo off
    echo cd /d "%%~dp0.."
    echo echo.
    echo echo  [1/2] Building KOF98_CS ^(C#^) ...
    echo echo.
    echo dotnet build KOF98_CS\KOF98_CS.csproj -c Release --nologo -v q
    echo if errorlevel 1 ^(
    echo     echo.
    echo     echo  [ERROR] C# build failed!
    echo     echo.
    echo     pause
    echo     exit /b 1
    echo ^)
    echo echo.
    echo echo  [2/2] Starting KOF98_CS ^(CS-simulation^) ...
    echo echo.
    echo echo  Controls: WASD/Arrows = move, J = LP, K = HP, U = LK, I = HK
    echo echo  Ctrl+C to stop
    echo echo.
    echo KOF98_CS\bin\Release\net8.0\KOF98_CS.exe --raylib
    echo echo.
    echo pause
) > KOF98_CS\run-kof98_cs.cmd
echo [OK] Generated KOF98_CS\run-kof98_cs.cmd

:: --- Step 5: Upsert VS Code debug configuration ---------------

:: Detect TargetFramework from KOF98_CS.csproj
set "NET_TFM=net8.0"
for /f "tokens=*" %%L in ('findstr /i "TargetFramework" KOF98_CS\KOF98_CS.csproj') do (
    for /f "tokens=2 delims=>" %%A in ("%%L") do (
        for /f "tokens=1 delims=<" %%T in ("%%A") do set "NET_TFM=%%T"
    )
)

if exist "KOF98\merge-vscode-config.ps1" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "KOF98\merge-vscode-config.ps1" -RepoRoot "%CD%" -Profile "KOF98_CS" -NetTfm "%NET_TFM%"
    if errorlevel 1 (
        echo [WARN] Could not upsert .vscode\launch.json / tasks.json
    ) else (
        echo [OK] Upserted .vscode\launch.json / .vscode\tasks.json  ^(KOF98_CS, TFM=%NET_TFM%^)
    )
) else (
    echo [WARN] Missing helper script: KOF98\merge-vscode-config.ps1
)

:: --- Done -----------------------------------------------------

echo.
echo  ========================================================
echo   KOF98_CS initialization complete!
echo  ========================================================
echo.
echo   [Generated]  KOF98_CS\run-kof98_cs.cmd
echo   [Generated]  .vscode\launch.json   (merged, includes KOF98_CS debug)
echo   [Generated]  .vscode\tasks.json    (merged, includes build-kof98_cs)
echo.
echo   Next steps:
echo     1. Double-click  KOF98_CS\run-kof98_cs.cmd  to launch the game
echo     2. Use WASD/Arrow keys to move (Idle / WalkForward / WalkBackward)
echo     3. Ctrl+C to stop
echo.
echo   VS Code C# debugging:
echo     1. Open the KOF98_CS folder in VS Code
echo     2. Set breakpoints in .cs files
echo     3. Press F5 -- pick "KOF98_CS: C# Debug (Raylib)"
echo.
echo   Command-line options:
echo     KOF98_CS.exe --raylib              Raylib window rendering
echo     KOF98_CS.exe                       Console (ASCII) rendering
echo     KOF98_CS.exe --headless            Simulation only (no rendering)
echo     KOF98_CS.exe --frames 600          Run for N frames then exit
echo.

pause
endlocal
