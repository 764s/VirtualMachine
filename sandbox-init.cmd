@echo off
setlocal enabledelayedexpansion

:: ============================================================
::  FFScript Sandbox — One-Click Initialization (Windows)
::
::  First-time setup after checkout:
::    1. Double-click this file
::    2. Double-click the generated  run-sandbox.cmd
::    3. (Optional) Open VS Code — debug extension auto-installed
:: ============================================================

cd /d "%~dp0"

echo.
echo  ========================================================
echo   FFScript Sandbox  -  One-Click Initialization
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

:: ─── Step 2: Generate Sandbox.csproj ────────────────────────

if exist "Sandbox\Sandbox.csproj" (
    echo [OK] Sandbox\Sandbox.csproj already exists, skipping.
) else (
    copy /y "Sandbox\Sandbox.csproj.template" "Sandbox\Sandbox.csproj" >nul
    if errorlevel 1 (
        echo [ERROR] Failed to copy Sandbox.csproj.template
        pause
        exit /b 1
    )
    echo [OK] Generated Sandbox\Sandbox.csproj
)

:: ─── Step 3: Build Sandbox ──────────────────────────────────

echo.
echo [*] Building Sandbox (Release)...
dotnet build Sandbox\Sandbox.csproj -c Release --nologo -v q
if errorlevel 1 (
    echo.
    echo [ERROR] Sandbox build failed!
    pause
    exit /b 1
)
echo [OK] Sandbox build succeeded.

:: ─── Step 4: Generate run-sandbox.cmd ───────────────────────

(
    echo @echo off
    echo cd /d "%%~dp0"
    echo echo.
    echo echo  Starting FFScript Sandbox ...
    echo echo.
    echo dotnet run --project Sandbox\Sandbox.csproj -c Release
    echo echo.
    echo pause
) > run-sandbox.cmd
echo [OK] Generated run-sandbox.cmd

:: ─── Step 5: Set up root .vscode/ ───────────────────────────

if not exist ".vscode" mkdir ".vscode"

:: Copy launch.json and tasks.json from Sandbox/.vscode/
copy /y "Sandbox\.vscode\launch.json" ".vscode\launch.json" >nul 2>&1
copy /y "Sandbox\.vscode\tasks.json"  ".vscode\tasks.json"  >nul 2>&1

if exist ".vscode\launch.json" (
    echo [OK] Copied .vscode/launch.json
) else (
    echo [WARN] Could not copy launch.json
)

:: ─── Step 6: Build StandaloneRunner (for DAP/LSP) ──────────

echo.
echo [*] Setting up StandaloneRunner (DAP/LSP server)...

if not exist "StandaloneRunner\StandaloneRunner.csproj" (
    :: Generate .csproj inline (same as CI)
    (
        echo ^<Project Sdk="Microsoft.NET.Sdk"^>
        echo   ^<PropertyGroup^>
        echo     ^<OutputType^>Exe^</OutputType^>
        echo     ^<TargetFramework^>net8.0^</TargetFramework^>
        echo     ^<EnableDefaultCompileItems^>false^</EnableDefaultCompileItems^>
        echo     ^<AllowUnsafeBlocks^>true^</AllowUnsafeBlocks^>
        echo   ^</PropertyGroup^>
        echo   ^<ItemGroup^>
        echo     ^<Compile Include="**/*.cs" /^>
        echo     ^<Compile Include="../Assets/Scripts/VM/**/*.cs" /^>
        echo   ^</ItemGroup^>
        echo ^</Project^>
    ) > "StandaloneRunner\StandaloneRunner.csproj"
    echo [OK] Generated StandaloneRunner\StandaloneRunner.csproj
)

dotnet build StandaloneRunner\StandaloneRunner.csproj -c Release --nologo -v q
if errorlevel 1 (
    echo [WARN] StandaloneRunner build failed — DAP/LSP debugging may not work.
) else (
    echo [OK] StandaloneRunner build succeeded.
)

:: ─── Step 7: Install VS Code extension (best-effort) ───────

echo.
echo [*] Attempting VS Code extension installation...

:: Check for npm
npm --version >nul 2>&1
if errorlevel 1 (
    echo [SKIP] npm not found — skipping VS Code extension installation.
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

:: Package VSIX (auto-installs @vscode/vsce if needed)
echo        Packaging extension with vsce...
call npx --yes @vscode/vsce package --no-dependencies -o ffvm-debug.vsix >nul 2>&1
if errorlevel 1 (
    echo [WARN] Failed to package VS Code extension.
    echo        Try manually: cd vscode-ffvm-debug ^&^& npx @vscode/vsce package
    popd
    goto :ext_done
)
popd

:: Check for VS Code CLI
code --version >nul 2>&1
if errorlevel 1 (
    echo [SKIP] VS Code CLI not found.
    echo        To install manually: code --install-extension vscode-ffvm-debug\ffvm-debug.vsix
    goto :ext_done
)

:: Install extension
code --install-extension "vscode-ffvm-debug\ffvm-debug.vsix" --force >nul 2>&1
if errorlevel 1 (
    echo [WARN] Failed to install VS Code extension.
    echo        Try manually: code --install-extension vscode-ffvm-debug\ffvm-debug.vsix
) else (
    echo [OK] VS Code extension installed successfully.
)

:ext_done

:: ─── Done ───────────────────────────────────────────────────

echo.
echo  ========================================================
echo   Initialization complete!
echo  ========================================================
echo.
echo   Next steps:
echo     1. Double-click  run-sandbox.cmd  to launch the sandbox
echo     2. Type [R] then Enter to compile and run your script
echo     3. Edit  Sandbox\scripts\main.ffs  to write your code
echo.
echo   VS Code debugging:
echo     1. Open this folder in VS Code
echo     2. Open  Sandbox\scripts\main.ffs
echo     3. Press F5 to start debugging
echo.

pause
endlocal
