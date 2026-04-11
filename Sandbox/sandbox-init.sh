#!/usr/bin/env bash
# ============================================================
#  FFScript Sandbox — One-Click Initialization (macOS / Linux)
#
#  First-time setup after checkout:
#    1. Run this script:  bash Sandbox/sandbox-init.sh
#    2. Run the generated: ./Sandbox/run-sandbox.sh
#    3. (Optional) Open VS Code — debug extension auto-installed
# ============================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR/.."

echo
echo " ========================================================"
echo "  FFScript Sandbox  —  One-Click Initialization"
echo " ========================================================"
echo

# ─── Step 1: Check .NET SDK ─────────────────────────────────

if ! command -v dotnet &>/dev/null; then
    echo "[ERROR] .NET SDK not found."
    echo "        Please install .NET 8.0 SDK from:"
    echo "        https://dotnet.microsoft.com/download/dotnet/8.0"
    exit 1
fi

echo "[OK] .NET SDK: $(dotnet --version)"

# ─── Step 2: Generate Sandbox.csproj ────────────────────────

if [ -f "Sandbox/Sandbox.csproj" ]; then
    echo "[OK] Sandbox/Sandbox.csproj already exists, skipping."
else
    cp "Sandbox/Sandbox.csproj.template" "Sandbox/Sandbox.csproj"
    echo "[OK] Generated Sandbox/Sandbox.csproj"
fi

# ─── Step 3: Build Sandbox ──────────────────────────────────

echo
echo "[*] Building Sandbox (Release)..."
dotnet build Sandbox/Sandbox.csproj -c Release --nologo -v q
echo "[OK] Sandbox build succeeded."

# ─── Step 4: Generate run-sandbox.sh ─────────────────────────

cat > Sandbox/run-sandbox.sh << 'RUNEOF'
#!/usr/bin/env bash
cd "$(dirname "$0")/.."
echo
echo " [1/2] Building Sandbox (C#) ..."
echo
dotnet build Sandbox/Sandbox.csproj -c Release --nologo -v q || { echo " [ERROR] C# build failed!"; exit 1; }
echo
echo " [2/2] Starting Sandbox ..."
echo
Sandbox/bin/Release/net10.0/Sandbox --debug
RUNEOF
chmod +x Sandbox/run-sandbox.sh
echo "[OK] Generated Sandbox/run-sandbox.sh"

# ─── Step 5: Set up root .vscode/ ───────────────────────────

mkdir -p .vscode

# Copy launch.json and tasks.json from Sandbox/.vscode/
cp -f Sandbox/.vscode/launch.json .vscode/launch.json 2>/dev/null && \
    echo "[OK] Copied .vscode/launch.json" || \
    echo "[WARN] Could not copy launch.json"

cp -f Sandbox/.vscode/tasks.json .vscode/tasks.json 2>/dev/null && \
    echo "[OK] Copied .vscode/tasks.json" || \
    echo "[WARN] Could not copy tasks.json"

# ─── Step 6: Build ffvm-cli (LSP / DAP server) ─────────────

echo
echo "[*] Building ffvm-cli (editor LSP & DAP support)..."

if dotnet build src/FFVM.Cli/FFVM.Cli.csproj -c Release --nologo -v q; then
    echo "[OK] ffvm-cli build succeeded."
else
    echo "[WARN] ffvm-cli build failed — editor LSP/DAP features will not work."
    echo "       You can retry manually: dotnet build src/FFVM.Cli/FFVM.Cli.csproj -c Release"
fi

# ─── Step 7: Build StandaloneRunner (for tests) ────────────

echo
echo "[*] Setting up StandaloneRunner (test runner)..."

if [ ! -f "StandaloneRunner/StandaloneRunner.csproj" ]; then
    # Generate .csproj inline (same as CI)
    cat > StandaloneRunner/StandaloneRunner.csproj << 'CSPROJEOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="**/*.cs" />
    <Compile Include="../Assets/Scripts/VM/**/*.cs" />
  </ItemGroup>
</Project>
CSPROJEOF
    echo "[OK] Generated StandaloneRunner/StandaloneRunner.csproj"
fi

if dotnet build StandaloneRunner/StandaloneRunner.csproj -c Release --nologo -v q; then
    echo "[OK] StandaloneRunner build succeeded."
else
    echo "[WARN] StandaloneRunner build failed — tests may not work."
fi

# ─── Step 8: Install VS Code extension (best-effort) ────────

echo
echo "[*] Attempting VS Code extension installation..."

install_extension() {
    # Check for npm
    if ! command -v npm &>/dev/null; then
        echo "[SKIP] npm not found — skipping VS Code extension installation."
        echo "       To install manually: cd vscode-ffvm-debug && npm install"
        return 0
    fi

    # Install extension dependencies
    pushd vscode-ffvm-debug > /dev/null
    if ! npm install --silent 2>/dev/null; then
        echo "[WARN] npm install failed in vscode-ffvm-debug/"
        popd > /dev/null
        return 0
    fi

    # Package VSIX (auto-installs @vscode/vsce if needed)
    echo "       Packaging extension with vsce..."
    if ! npx --yes @vscode/vsce package --no-dependencies -o ffvm-debug.vsix > /dev/null 2>&1; then
        echo "[WARN] Failed to package VS Code extension."
        echo "       Try manually: cd vscode-ffvm-debug && npx @vscode/vsce package"
        popd > /dev/null
        return 0
    fi
    popd > /dev/null

    # Check for VS Code CLI
    if ! command -v code &>/dev/null; then
        echo "[SKIP] VS Code CLI ('code') not found."
        echo "       To install manually: code --install-extension vscode-ffvm-debug/ffvm-debug.vsix"
        return 0
    fi

    # Install extension
    if code --install-extension "vscode-ffvm-debug/ffvm-debug.vsix" --force > /dev/null 2>&1; then
        echo "[OK] VS Code extension installed successfully."
    else
        echo "[WARN] Failed to install VS Code extension."
        echo "       Try manually: code --install-extension vscode-ffvm-debug/ffvm-debug.vsix"
    fi
}

install_extension

# ─── Done ────────────────────────────────────────────────────

echo
echo " ========================================================"
echo "  Initialization complete!"
echo " ========================================================"
echo
echo "  \033[92m[Generated]\033[0m  Sandbox/run-sandbox.sh"
echo
echo "  Next steps:"
echo "    1. Run  \033[93m./Sandbox/run-sandbox.sh\033[0m  to launch the sandbox"
echo "    2. Type [R] then Enter to compile and run your script"
echo "    3. Edit  Sandbox/scripts/main.ffs  to write your code"
echo
echo "  VS Code debugging:"
echo "    1. Open this folder in VS Code"
echo "    2. Open  Sandbox/scripts/main.ffs"
echo "    3. Press F5 to start debugging"
echo
