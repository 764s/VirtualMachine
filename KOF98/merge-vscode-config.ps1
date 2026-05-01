param(
    [Parameter(Mandatory = $true)]
    [string]$RepoRoot,

    [Parameter(Mandatory = $true)]
    [ValidateSet('KOF98', 'KOF98_CS')]
    [string]$Profile,

    [string]$NetTfm = 'net8.0'
)

$ErrorActionPreference = 'Stop'

function Read-JsonOrDefault {
    param(
        [string]$Path,
        [hashtable]$Default
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]$Default
    }

    $raw = Get-Content -LiteralPath $Path -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return [pscustomobject]$Default
    }

    try {
        return $raw | ConvertFrom-Json
    }
    catch {
        throw "Invalid JSON in file: $Path"
    }
}

function Ensure-Property {
    param(
        [object]$Object,
        [string]$Name,
        [object]$Value
    )

    if (-not ($Object.PSObject.Properties.Name -contains $Name)) {
        $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value
    }
    elseif ($null -eq $Object.$Name) {
        $Object.$Name = $Value
    }
}

$vscodeDir = Join-Path $RepoRoot '.vscode'
if (-not (Test-Path -LiteralPath $vscodeDir)) {
    New-Item -ItemType Directory -Path $vscodeDir | Out-Null
}

$launchPath = Join-Path $vscodeDir 'launch.json'
$tasksPath = Join-Path $vscodeDir 'tasks.json'

$launch = Read-JsonOrDefault -Path $launchPath -Default @{
    version = '0.2.0'
    configurations = @()
    compounds = @()
}
Ensure-Property -Object $launch -Name 'version' -Value '0.2.0'
Ensure-Property -Object $launch -Name 'configurations' -Value @()
Ensure-Property -Object $launch -Name 'compounds' -Value @()

$tasks = Read-JsonOrDefault -Path $tasksPath -Default @{
    version = '2.0.0'
    tasks = @()
}
Ensure-Property -Object $tasks -Name 'version' -Value '2.0.0'
Ensure-Property -Object $tasks -Name 'tasks' -Value @()

$existingConfigs = @($launch.configurations)
$existingCompounds = @($launch.compounds)
$existingTasks = @($tasks.tasks)

switch ($Profile) {
    'KOF98_CS' {
        $namesToReplace = @(
            'KOF98_CS: C# Debug (Raylib)',
            'KOF98_CS: C# Debug (Console)',
            'KOF98_CS: C# Debug (Headless 600 frames)'
        )
        $taskLabelsToReplace = @('build-kof98_cs')

        $newConfigs = @(
            [pscustomobject]@{
                name = 'KOF98_CS: C# Debug (Raylib)'
                type = 'coreclr'
                request = 'launch'
                program = ('${workspaceFolder}/KOF98_CS/bin/Debug/' + $NetTfm + '/KOF98_CS')
                args = @('--raylib')
                cwd = '${workspaceFolder}'
                console = 'integratedTerminal'
                preLaunchTask = 'build-kof98_cs'
            },
            [pscustomobject]@{
                name = 'KOF98_CS: C# Debug (Console)'
                type = 'coreclr'
                request = 'launch'
                program = ('${workspaceFolder}/KOF98_CS/bin/Debug/' + $NetTfm + '/KOF98_CS')
                args = @()
                cwd = '${workspaceFolder}'
                console = 'integratedTerminal'
                preLaunchTask = 'build-kof98_cs'
            },
            [pscustomobject]@{
                name = 'KOF98_CS: C# Debug (Headless 600 frames)'
                type = 'coreclr'
                request = 'launch'
                program = ('${workspaceFolder}/KOF98_CS/bin/Debug/' + $NetTfm + '/KOF98_CS')
                args = @('--headless', '--frames', '600')
                cwd = '${workspaceFolder}'
                console = 'integratedTerminal'
                preLaunchTask = 'build-kof98_cs'
            }
        )

        $newTasks = @(
            [pscustomobject]@{
                label = 'build-kof98_cs'
                type = 'shell'
                command = 'dotnet'
                args = @('build', '${workspaceFolder}/KOF98_CS/KOF98_CS.csproj', '-c', 'Debug')
                problemMatcher = '$msCompile'
            }
        )
    }
    'KOF98' {
        $namesToReplace = @(
            'KOF98: Attach FFVM',
            'KOF98: C# Debug (Raylib)',
            'KOF98: C# + DAP Launch',
            'KOF98: FFVM Attach (auto)'
        )
        $compoundNamesToReplace = @('KOF98: C# + FFVM Debug')
        $taskLabelsToReplace = @('build-kof98', 'wait-for-dap')

        $newConfigs = @(
            [pscustomobject]@{
                name = 'KOF98: Attach FFVM'
                type = 'ffvm'
                request = 'attach'
                port = 4711
            },
            [pscustomobject]@{
                name = 'KOF98: C# Debug (Raylib)'
                type = 'coreclr'
                request = 'launch'
                program = ('${workspaceFolder}/KOF98/bin/Debug/' + $NetTfm + '/KOF98')
                args = @('--raylib', '--debug-nowait')
                cwd = '${workspaceFolder}'
                console = 'integratedTerminal'
                preLaunchTask = 'build-kof98'
            },
            [pscustomobject]@{
                name = 'KOF98: C# + DAP Launch'
                type = 'coreclr'
                request = 'launch'
                program = ('${workspaceFolder}/KOF98/bin/Debug/' + $NetTfm + '/KOF98')
                args = @('--raylib', '--debug')
                cwd = '${workspaceFolder}'
                console = 'integratedTerminal'
                preLaunchTask = 'build-kof98'
                presentation = [pscustomobject]@{ hidden = $true }
            },
            [pscustomobject]@{
                name = 'KOF98: FFVM Attach (auto)'
                type = 'ffvm'
                request = 'attach'
                port = 4711
                preLaunchTask = 'wait-for-dap'
                presentation = [pscustomobject]@{ hidden = $true }
            }
        )

        $newCompounds = @(
            [pscustomobject]@{
                name = 'KOF98: C# + FFVM Debug'
                configurations = @('KOF98: C# + DAP Launch', 'KOF98: FFVM Attach (auto)')
                stopAll = $true
            }
        )

        $newTasks = @(
            [pscustomobject]@{
                label = 'build-kof98'
                type = 'shell'
                command = 'dotnet'
                args = @('build', '${workspaceFolder}/KOF98/KOF98.csproj', '-c', 'Debug')
                group = [pscustomobject]@{ kind = 'build'; isDefault = $true }
                problemMatcher = '$msCompile'
            },
            [pscustomobject]@{
                label = 'wait-for-dap'
                type = 'shell'
                command = 'powershell'
                args = @(
                    '-NoProfile',
                    '-Command',
                    "for (`$i = 0; `$i -lt 60; `$i++) { if (Get-NetTCPConnection -LocalPort 4711 -State Listen -ErrorAction SilentlyContinue) { exit 0 }; Start-Sleep -Milliseconds 500 }; Write-Error 'DAP server not ready after 30s'; exit 1"
                )
                presentation = [pscustomobject]@{ reveal = 'silent' }
                problemMatcher = @()
            }
        )
    }
}

$launch.configurations = @($existingConfigs | Where-Object { $_.name -notin $namesToReplace }) + $newConfigs

if ($Profile -eq 'KOF98') {
    $launch.compounds = @($existingCompounds | Where-Object { $_.name -notin $compoundNamesToReplace }) + $newCompounds
}
else {
    $launch.compounds = $existingCompounds
}

$tasks.tasks = @($existingTasks | Where-Object { $_.label -notin $taskLabelsToReplace }) + $newTasks

$launch | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $launchPath -Encoding utf8
$tasks | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $tasksPath -Encoding utf8

Write-Host "[OK] Upserted VS Code configs for profile: $Profile"
