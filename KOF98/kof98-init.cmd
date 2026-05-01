@echo off
setlocal

:: ============================================================
::  KOF98 Practice -- One-Click Console (Windows)
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

:: --- Step 2: Generate KOF98.csproj ----------------------------

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

:: --- Step 3: Build KOF98 --------------------------------------

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
    echo    - Ensure src\FFVM\FFVM.csproj exists ^(FFVM core library^).
    echo.
    pause
    exit /b 1
)
echo [OK] KOF98 build succeeded.

:: --- Step 4: Generate run-kof98.cmd ---------------------------

(
    echo @echo off
    echo cd /d "%%~dp0.."
    echo echo.
    echo echo  [1/2] Building KOF98 ^(C#^) ...
    echo echo.
    echo dotnet build KOF98\KOF98.csproj -c Release --nologo -v q
    echo if errorlevel 1 ^(
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
    echo KOF98\bin\Release\net8.0\KOF98.exe --raylib
    echo echo.
    echo pause
) > KOF98\run-kof98.cmd
echo [OK] Generated KOF98\run-kof98.cmd

:: --- Step 5: Upsert VS Code debug configuration -----------------

:: Detect TargetFramework from KOF98.csproj (e.g. net8.0)
set "NET_TFM=net8.0"
for /f "tokens=*" %%L in ('findstr /i "TargetFramework" KOF98\KOF98.csproj') do (
    for /f "tokens=2 delims=>" %%A in ("%%L") do (
        for /f "tokens=1 delims=<" %%T in ("%%A") do set "NET_TFM=%%T"
    )
)

if exist "KOF98\merge-vscode-config.ps1" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "KOF98\merge-vscode-config.ps1" -RepoRoot "%CD%" -Profile "KOF98" -NetTfm "%NET_TFM%"
    if errorlevel 1 (
        echo [WARN] Could not upsert .vscode\launch.json / tasks.json
    ) else (
        echo [OK] Upserted .vscode\launch.json / .vscode\tasks.json  ^(KOF98 + FFVM, TFM=%NET_TFM%^)
    )
) else (
    echo [WARN] Missing helper script: KOF98\merge-vscode-config.ps1
)

:: --- Step 6: Build ffvm-cli (LSP / DAP server) ----------------

echo.
echo [*] Building ffvm-cli (editor LSP ^& DAP support)...

:: Kill running ffvm-cli so the build can overwrite the DLL
taskkill /F /IM ffvm-cli.exe >nul 2>&1

dotnet build src\FFVM.Cli\FFVM.Cli.csproj -c Debug --nologo -v q
if errorlevel 1 (
    echo [WARN] ffvm-cli build failed -- editor LSP/DAP features will not work.
    echo        You can retry manually: dotnet build src\FFVM.Cli\FFVM.Cli.csproj -c Debug
) else (
    echo [OK] ffvm-cli build succeeded.
)

:: --- Step 7: Install VS Code extension (best-effort) ----------

echo.
echo [*] Attempting VS Code extension installation...

:: Check for npm
call npm --version >nul 2>&1
if errorlevel 1 (
    echo [SKIP] npm not found -- skipping VS Code extension installation.
    echo        To install manually: cd vscode-ffvm-debug ^&^& npm install
    goto :ext_done
)

:: Install extension dependencies
pushd vscode-ffvm-debug
call npm install --silent 2>nul
if errorlevel 1 (
    echo [WARN] npm install failed in vscode-ffvm-debug/
    popd
    goto :ext_done
)

:: Package VSIX
echo        Packaging extension with vsce...
call npx --yes @vscode/vsce package -o ffvm-debug.vsix --allow-missing-repository --skip-license >nul 2>&1
if errorlevel 1 (
    echo [WARN] Failed to package VS Code extension.
    echo        Try manually: cd vscode-ffvm-debug ^&^& npx @vscode/vsce package
    popd
    goto :ext_done
)
popd

:: Check for VS Code CLI
call code --version >nul 2>&1
if errorlevel 1 (
    echo [SKIP] VS Code CLI not found.
    echo        To install manually: code --install-extension vscode-ffvm-debug\ffvm-debug.vsix
    goto :ext_done
)

:: Remove old extension to ensure --force actually overwrites
for /d %%D in ("%USERPROFILE%\.vscode\extensions\ffvm.ffvm-debug-*") do (
    rmdir /s /q "%%D" >nul 2>&1
)

:: Install extension via CLI, then verify; fall back to manual extract
call code --install-extension "vscode-ffvm-debug\ffvm-debug.vsix" --force >nul 2>&1

:: Verify installation succeeded (check extension directory exists with node_modules)
set "EXT_DIR=%USERPROFILE%\.vscode\extensions\ffvm.ffvm-debug-0.2.0"
if exist "%EXT_DIR%\node_modules" goto :ext_verify
echo        CLI install incomplete, extracting VSIX manually...
if not exist "%EXT_DIR%" mkdir "%EXT_DIR%"
powershell -NoProfile -Command "Add-Type -A System.IO.Compression.FileSystem; $z=[IO.Compression.ZipFile]::OpenRead('vscode-ffvm-debug\ffvm-debug.vsix'); $m=$z.GetEntry('extension.vsixmanifest'); [IO.Compression.ZipFileExtensions]::ExtractToFile($m,\"$env:USERPROFILE\.vscode\extensions\ffvm.ffvm-debug-0.2.0\.vsixmanifest\",$true); foreach($e in $z.Entries){if($e.FullName.StartsWith('extension/') -and -not $e.FullName.EndsWith('/')){$r=$e.FullName.Substring(10);$t=Join-Path $env:USERPROFILE\.vscode\extensions\ffvm.ffvm-debug-0.2.0 $r;$d=Split-Path $t;if(!(Test-Path $d)){md $d -Force|Out-Null};[IO.Compression.ZipFileExtensions]::ExtractToFile($e,$t,$true)}}; $z.Dispose()" >nul 2>&1

:ext_verify
if exist "%EXT_DIR%\node_modules" (
    echo [OK] VS Code extension installed successfully.
) else (
    echo [WARN] Failed to install VS Code extension.
    echo        Try manually: code --install-extension vscode-ffvm-debug\ffvm-debug.vsix
)

:ext_done

:: --- Done -----------------------------------------------------

echo.
echo  ========================================================
echo   Initialization complete!
echo  ========================================================
echo.
echo   [92m[Generated][0m  KOF98\run-kof98.cmd
echo   [92m[Generated][0m  .vscode\launch.json  (C# + FFVM debug)
echo.
echo   Next steps:
echo     1. Double-click  [93mKOF98\run-kof98.cmd[0m  to launch the game
echo     2. Use WASD/Arrow keys to move, J/K/U/I to attack
echo     3. Ctrl+C to stop
echo.
echo   VS Code debugging (C# + FFScript):
echo     1. Open this folder in VS Code
echo     2. Set breakpoints in .cs and .ffs files
echo     3. Press F5 -- select "KOF98: C# + FFVM Debug"
echo     4. The game window opens, breakpoints work in both C# and FFScript
echo.
echo   Command-line options:
echo     KOF98.exe --raylib             Raylib window rendering
echo     KOF98.exe --raylib --debug     Raylib + DAP debugger (port 4711)
echo     KOF98.exe --headless           Simulation only (no rendering)
echo     KOF98.exe --frames 600         Run for N frames then exit
echo.

pause
endlocal