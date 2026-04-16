# Path Tooling Constraints

Purpose: avoid high-frequency path/URI logic being reimplemented across handlers, compiler, and database glue.

## Mandatory Rules

1. Never compare raw path strings directly for dependency/index keys.
2. Always normalize with `WorkspacePathTool.NormalizePath` before storing or looking up path keys.
3. Use `WorkspacePathTool.UriToPath` and `WorkspacePathTool.PathToFileUri` for protocol boundary conversion.
4. Use `WorkspacePathTool.ResolvePath` for base + relative path resolution.
5. Avoid direct `Path.GetFullPath` in LSP/DB dependency-graph code unless wrapped by `WorkspacePathTool`.

## Recommended Review Checklist

1. Any new map key for file identity uses normalized path.
2. didOpen/didChange/didClose/didChangeWatchedFiles use the same normalized path format.
3. Include dependency graph keys match compiler/preprocessor resolved paths.
4. Relative project paths and `file:///` URI conversions are tested on Windows and Unix-style cases.

## Scope

This file governs path handling in:

- `Assets/Scripts/VM/Debug/LspServer.cs`
- `Assets/Scripts/VM/Compiler/ProjectFile.cs`
- `Assets/Scripts/VM/Compiler/Preprocessor.cs`
- Database bridge/orchestrator path touchpoints when they start storing path keys.
