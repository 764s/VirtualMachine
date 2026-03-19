# VirtualMachine

## Run VM tests without Unity

This project includes a standalone VM test runner, so you can run tests without opening the Unity Editor.

### Method 1: VSCode menu

1. Open this repository in VSCode
2. Use the top menu: `Terminal` -> `Run Task`
3. Select `Run VM Tests`

### Method 2: BAT script

Run the helper script from the repository root:

```bat
run-vm-tests.cmd
```

### Method 3: Command line

Run from the repository root:

```bash
dotnet run --project StandaloneRunner
```

The runner in [StandaloneRunner/](StandaloneRunner/) executes the VM tests from [Assets/Scripts/VM/Tests/TreeWalkerTests.cs](Assets/Scripts/VM/Tests/TreeWalkerTests.cs).
