using System;
using System.Collections.Generic;
using System.IO;
using FFVM.AST;

namespace FFVM.Compiler
{
    /// <summary>
    /// Abstraction for reading included files.
    /// </summary>
    public interface IFileResolver
    {
        /// <summary>
        /// Read the contents of a file at the given path.
        /// Returns null if the file is not found.
        /// </summary>
        string ReadFile(string path);

        /// <summary>
        /// Resolve the given path to an absolute filesystem path.
        /// Returns the resolved path if the file exists, or null if not found.
        /// Used by the Preprocessor to record accurate OriginFile paths for cross-file navigation.
        /// </summary>
        string ResolveFilePath(string path);
    }

    /// <summary>
    /// In-memory file resolver for tests.
    /// Maps logical path (e.g. "configs/base") to source text.
    /// </summary>
    public class DictionaryFileResolver : IFileResolver
    {
        private readonly Dictionary<string, string> _files;

        public DictionaryFileResolver(Dictionary<string, string> files)
        {
            _files = files ?? new Dictionary<string, string>();
        }

        public string ReadFile(string path)
        {
            string result;
            return _files.TryGetValue(path, out result) ? result : null;
        }

        public string ResolveFilePath(string path)
        {
            return _files.ContainsKey(path) ? path : null;
        }
    }

    /// <summary>
    /// DX4-P0: Filesystem-based file resolver for include directives.
    /// Resolves include paths relative to a base directory.
    /// Auto-appends .ffs extension if not present.
    /// Validates resolved path stays within baseDir (prevents path traversal).
    /// </summary>
    public class FileSystemFileResolver : IFileResolver
    {
        private readonly string _baseDir;

        public FileSystemFileResolver(string baseDir)
        {
            _baseDir = Path.GetFullPath(baseDir);
        }

        public string ReadFile(string path)
        {
            string fullPath = ResolveFullPath(path);
            if (fullPath == null) return null;
            return File.ReadAllText(fullPath);
        }

        public string ResolveFilePath(string path)
        {
            return ResolveFullPath(path);
        }

        private string ResolveFullPath(string path)
        {
            string fullPath = Path.GetFullPath(Path.Combine(_baseDir, path));
            // Append .ffs extension if not present (include "common/constants" → common/constants.ffs)
            if (!fullPath.EndsWith(".ffs", StringComparison.OrdinalIgnoreCase))
                fullPath += ".ffs";
            // Validate path stays within base directory (prevent path traversal)
            if (!fullPath.StartsWith(_baseDir, StringComparison.OrdinalIgnoreCase))
                return null;
            if (!File.Exists(fullPath)) return null;
            return fullPath;
        }
    }

    /// <summary>
    /// Lang-2: Preprocessor that recursively expands include directives.
    ///
    /// Processing:
    ///   1. Parse the main file → ModuleNode (with Imports list).
    ///   2. For each import, recursively resolve the included file.
    ///   3. Merge all declarations into a single ModuleNode with override semantics.
    ///   4. Return the merged ModuleNode (no Imports — all resolved).
    ///
    /// Override rules (Lang-16):
    ///   - Cross-file: requires explicit 'override' keyword regardless of file position in include chain.
    ///   - Same-file: duplicate declaration is a compile error.
    ///   - var cannot override const; const cannot override var.
    /// </summary>
    public class Preprocessor
    {
        private readonly IFileResolver _fileResolver;
        private readonly List<string> _errors;

        public Preprocessor(IFileResolver fileResolver)
        {
            _fileResolver = fileResolver;
            _errors = new List<string>();
        }

        /// <summary>
        /// Resolve all includes starting from the given source text and return a merged ModuleNode.
        /// </summary>
        /// <param name="source">Main file source text</param>
        /// <param name="filePath">Logical path of the main file (for diagnostics and cycle detection)</param>
        /// <param name="errors">Output parse/preprocess errors</param>
        /// <returns>Merged ModuleNode with all includes resolved</returns>
        public ModuleNode Resolve(string source, string filePath, out List<string> errors)
        {
            _errors.Clear();
            var stack = new List<string>();
            var result = ResolveRecursive(source, filePath, stack);
            errors = new List<string>(_errors);
            return result;
        }

        private ModuleNode ResolveRecursive(string source, string filePath, List<string> stack)
        {
            // Cycle detection: check if filePath is in the current stack (active include chain)
            for (int i = 0; i < stack.Count; i++)
            {
                if (stack[i] == filePath)
                {
                    stack.Add(filePath);
                    _errors.Add($"Circular include detected: {string.Join(" -> ", stack)}");
                    stack.RemoveAt(stack.Count - 1);
                    return new ModuleNode(filePath);
                }
            }

            stack.Add(filePath);

            // Parse this file
            var parser = new Parser();
            var module = parser.Parse(source, out var parseErrors);
            if (parseErrors != null && parseErrors.Count > 0)
            {
                for (int i = 0; i < parseErrors.Count; i++)
                    _errors.Add($"[{filePath}] {parseErrors[i]}");
                return module;
            }

            // If no imports, return with correct file path
            if (module.Imports.Count == 0)
            {
                stack.RemoveAt(stack.Count - 1);
                // Re-wrap with correct filePath (Parser uses "script" by default)
                var wrapped = new ModuleNode(filePath);
                for (int i = 0; i < module.Structs.Count; i++)
                {
                    module.Structs[i].OriginFile = filePath;  // Lang-15
                    // Lang-16: override without includes is invalid
                    if (module.Structs[i].IsOverride)
                        _errors.Add($"[{filePath}] 'override' on struct '{module.Structs[i].Name}' but no included file provides a declaration to override");
                    wrapped.Structs.Add(module.Structs[i]);
                }
                for (int i = 0; i < module.Functions.Count; i++)
                {
                    module.Functions[i].OriginFile = filePath;  // Lang-15
                    if (module.Functions[i].IsOverride)
                        _errors.Add($"[{filePath}] 'override' on function '{module.Functions[i].Name}' but no included file provides a declaration to override");
                    wrapped.Functions.Add(module.Functions[i]);
                }
                for (int i = 0; i < module.ModuleVariables.Count; i++)
                {
                    module.ModuleVariables[i].OriginFile = filePath;  // Lang-15
                    if (module.ModuleVariables[i].IsOverride)
                    {
                        string kind = module.ModuleVariables[i].IsConst ? "const" : "var";
                        _errors.Add($"[{filePath}] 'override' on {kind} '{module.ModuleVariables[i].Name}' but no included file provides a declaration to override");
                    }
                    wrapped.ModuleVariables.Add(module.ModuleVariables[i]);
                }
                for (int i = 0; i < module.Enums.Count; i++)
                {
                    module.Enums[i].OriginFile = filePath;  // Lang-15
                    if (module.Enums[i].IsOverride)
                        _errors.Add($"[{filePath}] 'override' on enum '{module.Enums[i].Name}' but no included file provides a declaration to override");
                    wrapped.Enums.Add(module.Enums[i]);
                }
                return wrapped;
            }

            // Resolve each import depth-first, then merge
            // Final merged module: includes first (in order), then main file's own declarations
            var merged = new ModuleNode(filePath);

            // Track declaration sources for same-file redefinition detection
            // key = name, value = source file path
            var constSources = new Dictionary<string, string>();
            var funcSources = new Dictionary<string, string>();
            var structSources = new Dictionary<string, string>();
            var varSources = new Dictionary<string, string>();
            var enumSources = new Dictionary<string, string>();

            // Track const vs var kind for cross-kind override rejection
            var declKinds = new Dictionary<string, bool>(); // name → isConst

            for (int i = 0; i < module.Imports.Count; i++)
            {
                string importPath = module.Imports[i].ModulePath;
                string alias = module.Imports[i].Alias;  // Lang-17
                string importSource = _fileResolver != null ? _fileResolver.ReadFile(importPath) : null;
                if (importSource == null)
                {
                    _errors.Add($"[{filePath}] Include file not found: \"{importPath}\"");
                    continue;
                }

                // Resolve to actual filesystem path for accurate OriginFile (cross-file navigation)
                string resolvedImportPath = _fileResolver.ResolveFilePath(importPath) ?? importPath;
                var importModule = ResolveRecursive(importSource, resolvedImportPath, stack);
                if (_errors.Count > 0) continue;

                // Lang-17: aliased include → store in AliasedModules, no flat merge
                if (alias != null)
                {
                    if (merged.AliasedModules.ContainsKey(alias))
                    {
                        _errors.Add($"[{filePath}] Duplicate include alias '{alias}'");
                        continue;
                    }
                    merged.AliasedModules[alias] = importModule;
                    continue;
                }

                // Merge imported declarations
                MergeDeclarations(merged, importModule, constSources, funcSources, structSources, varSources, enumSources, declKinds, filePath);
            }

            // Now merge main file's own declarations (these override includes)
            MergeMainDeclarations(merged, module, constSources, funcSources, structSources, varSources, enumSources, declKinds, filePath);

            stack.RemoveAt(stack.Count - 1);
            return merged;
        }

        /// <summary>
        /// Merge declarations from an included module into the target.
        /// Included declarations can override each other (cross-file override).
        /// </summary>
        private void MergeDeclarations(
            ModuleNode target, ModuleNode source,
            Dictionary<string, string> constSources,
            Dictionary<string, string> funcSources,
            Dictionary<string, string> structSources,
            Dictionary<string, string> varSources,
            Dictionary<string, string> enumSources,
            Dictionary<string, bool> declKinds,
            string mainFilePath)
        {
            string srcFile = source.FilePath;

            // Merge consts and vars (ModuleVariables)
            for (int i = 0; i < source.ModuleVariables.Count; i++)
            {
                var v = source.ModuleVariables[i];
                MergeModuleVariable(target, v, constSources, varSources, declKinds, srcFile, isMainFile: false);
            }

            // Merge funcs
            for (int i = 0; i < source.Functions.Count; i++)
            {
                var f = source.Functions[i];
                MergeFunc(target, f, funcSources, srcFile, isMainFile: false);
            }

            // Merge structs
            for (int i = 0; i < source.Structs.Count; i++)
            {
                var s = source.Structs[i];
                MergeStruct(target, s, structSources, srcFile, isMainFile: false);
            }

            // Lang-13: Merge enums with cross-file override (same semantics as structs)
            for (int i = 0; i < source.Enums.Count; i++)
            {
                var e = source.Enums[i];
                MergeEnum(target, e, enumSources, srcFile, isMainFile: false);
            }
        }

        /// <summary>
        /// Merge the main file's own declarations. Same-file duplicates are errors.
        /// </summary>
        private void MergeMainDeclarations(
            ModuleNode target, ModuleNode mainModule,
            Dictionary<string, string> constSources,
            Dictionary<string, string> funcSources,
            Dictionary<string, string> structSources,
            Dictionary<string, string> varSources,
            Dictionary<string, string> enumSources,
            Dictionary<string, bool> declKinds,
            string mainFilePath)
        {
            string srcFile = mainFilePath;

            for (int i = 0; i < mainModule.ModuleVariables.Count; i++)
            {
                var v = mainModule.ModuleVariables[i];
                // Lang-18: aliased override → apply to aliased module
                if (v.AliasTarget != null)
                {
                    ApplyAliasedVarOverride(target, v, srcFile);
                    continue;
                }
                MergeModuleVariable(target, v, constSources, varSources, declKinds, srcFile, isMainFile: true);
            }

            for (int i = 0; i < mainModule.Functions.Count; i++)
            {
                var f = mainModule.Functions[i];
                // Lang-18: aliased override → apply to aliased module
                if (f.AliasTarget != null)
                {
                    ApplyAliasedFuncOverride(target, f, srcFile);
                    continue;
                }
                MergeFunc(target, f, funcSources, srcFile, isMainFile: true);
            }

            for (int i = 0; i < mainModule.Structs.Count; i++)
            {
                var s = mainModule.Structs[i];
                // Lang-18: aliased override → apply to aliased module
                if (s.AliasTarget != null)
                {
                    ApplyAliasedStructOverride(target, s, srcFile);
                    continue;
                }
                MergeStruct(target, s, structSources, srcFile, isMainFile: true);
            }

            // Lang-13: Merge enums from main file
            for (int i = 0; i < mainModule.Enums.Count; i++)
            {
                var e = mainModule.Enums[i];
                // Lang-18: aliased override → apply to aliased module
                if (e.AliasTarget != null)
                {
                    ApplyAliasedEnumOverride(target, e, srcFile);
                    continue;
                }
                MergeEnum(target, e, enumSources, srcFile, isMainFile: true);
            }
        }

        private void MergeModuleVariable(
            ModuleNode target, VarDeclStmt v,
            Dictionary<string, string> constSources,
            Dictionary<string, string> varSources,
            Dictionary<string, bool> declKinds,
            string srcFile,
            bool isMainFile)
        {
            // Lang-15: stamp OriginFile (preserve original if already set — diamond include)
            if (v.OriginFile == null) v.OriginFile = srcFile;
            string origin = v.OriginFile;

            string name = v.Name;

            // Lang-15: private declarations never conflict with declarations from other files.
            // They are always added (not replaced). Same-file redefinition is still an error.
            if (v.IsPrivate)
            {
                // Check same-file redefinition for private: use qualified key
                string qualKey = name + "\0" + origin;
                var sources = v.IsConst ? constSources : varSources;
                string existingFile;
                if (sources.TryGetValue(qualKey, out existingFile))
                {
                    string kind = v.IsConst ? "const" : "var";
                    _errors.Add($"[{origin}] Duplicate private {kind} '{name}' in the same file");
                    return;
                }
                target.ModuleVariables.Add(v);
                sources[qualKey] = origin;
                declKinds[qualKey] = v.IsConst;
                return;
            }

            var varSrc = v.IsConst ? constSources : varSources;
            var otherSources = v.IsConst ? varSources : constSources;

            // Check const/var kind conflict
            bool existingKind;
            if (declKinds.TryGetValue(name, out existingKind) && existingKind != v.IsConst)
            {
                if (v.IsConst)
                    _errors.Add($"[{origin}] Cannot override var '{name}' with const");
                else
                    _errors.Add($"[{origin}] Cannot override const '{name}' with var");
                return;
            }

            // Check same-file redefinition vs diamond include
            string existFile;
            if (varSrc.TryGetValue(name, out existFile) && existFile == origin)
            {
                if (isMainFile)
                {
                    // True same-file redefinition → error
                    string kind = v.IsConst ? "const" : "var";
                    _errors.Add($"[{origin}] Duplicate {kind} '{name}' in the same file");
                    return;
                }
                // Diamond include: same origin arriving through different include paths → skip silently
                return;
            }

            // Cross-file override or new declaration — replace
            if (varSrc.ContainsKey(name))
            {
                // Lang-16: require explicit override keyword for any cross-file replacement
                if (!v.IsOverride)
                {
                    string kind = v.IsConst ? "const" : "var";
                    string existingFrom = varSrc[name];
                    _errors.Add($"[{origin}] {kind} '{name}' conflicts with declaration from '{existingFrom}'. Use 'override {kind}' to intentionally replace it, or 'private {kind}' to keep both independently");
                    return;
                }

                // Replace existing in target (only public can override public)
                for (int j = 0; j < target.ModuleVariables.Count; j++)
                {
                    if (target.ModuleVariables[j].Name == name && !target.ModuleVariables[j].IsPrivate)
                    {
                        target.ModuleVariables[j] = v;
                        break;
                    }
                }
            }
            else
            {
                // Lang-16: override on a new declaration (nothing to override) → error
                // Guard on isMainFile: resolved child modules propagate IsOverride through include chains,
                // but the "no prior declaration" check only applies to the file's own declarations.
                if (isMainFile && v.IsOverride)
                {
                    string kind = v.IsConst ? "const" : "var";
                    _errors.Add($"[{origin}] 'override' on {kind} '{name}' but no prior declaration exists to override");
                    return;
                }
                target.ModuleVariables.Add(v);
            }

            varSrc[name] = origin;
            declKinds[name] = v.IsConst;
        }

        private void MergeFunc(
            ModuleNode target, FuncDecl f,
            Dictionary<string, string> funcSources,
            string srcFile,
            bool isMainFile)
        {
            // Lang-15: stamp OriginFile (preserve original if already set — diamond include)
            if (f.OriginFile == null) f.OriginFile = srcFile;
            string origin = f.OriginFile;

            string name = f.Name;

            // Lang-15: private functions never conflict with functions from other files.
            if (f.IsPrivate)
            {
                string qualKey = name + "\0" + origin;
                if (funcSources.ContainsKey(qualKey))
                {
                    _errors.Add($"[{origin}] Duplicate private function '{name}' in the same file");
                    return;
                }
                target.Functions.Add(f);
                funcSources[qualKey] = origin;
                return;
            }

            // Check same-file redefinition vs diamond include
            string existingFile;
            if (funcSources.TryGetValue(name, out existingFile) && existingFile == origin)
            {
                if (isMainFile)
                {
                    _errors.Add($"[{origin}] Duplicate function '{name}' in the same file");
                    return;
                }
                // Diamond include → skip silently
                return;
            }

            // Cross-file override or new declaration — replace (public only)
            if (funcSources.ContainsKey(name))
            {
                string existingFrom = funcSources[name];

                // Lang-16: require explicit override keyword for any cross-file replacement
                if (!f.IsOverride)
                {
                    _errors.Add($"[{origin}] Function '{name}' conflicts with declaration from '{existingFrom}'. Use 'override func' to intentionally replace it, or 'private func' to keep both independently");
                    return;
                }

                for (int j = 0; j < target.Functions.Count; j++)
                {
                    if (target.Functions[j].Name == name && !target.Functions[j].IsPrivate)
                    {
                        target.Functions[j] = f;
                        break;
                    }
                }
            }
            else
            {
                // Lang-16: override on a new declaration (nothing to override) → error
                // Guard on isMainFile: resolved child modules propagate IsOverride through include chains,
                // but the "no prior declaration" check only applies to the file's own declarations.
                if (isMainFile && f.IsOverride)
                {
                    _errors.Add($"[{origin}] 'override' on function '{name}' but no prior declaration exists to override");
                    return;
                }
                target.Functions.Add(f);
            }

            funcSources[name] = origin;
        }

        private void MergeStruct(
            ModuleNode target, StructDecl s,
            Dictionary<string, string> structSources,
            string srcFile,
            bool isMainFile)
        {
            // Lang-15: stamp OriginFile (preserve original if already set — diamond include)
            if (s.OriginFile == null) s.OriginFile = srcFile;
            string origin = s.OriginFile;

            string name = s.Name;

            // Lang-15: private structs never conflict with structs from other files.
            if (s.IsPrivate)
            {
                string qualKey = name + "\0" + origin;
                if (structSources.ContainsKey(qualKey))
                {
                    _errors.Add($"[{origin}] Duplicate private struct '{name}' in the same file");
                    return;
                }
                target.Structs.Add(s);
                structSources[qualKey] = origin;
                return;
            }

            // Check same-file redefinition vs diamond include
            string existingFile;
            if (structSources.TryGetValue(name, out existingFile) && existingFile == origin)
            {
                if (isMainFile)
                {
                    _errors.Add($"[{origin}] Duplicate struct '{name}' in the same file");
                    return;
                }
                // Diamond include → skip silently
                return;
            }

            // Cross-file override or new declaration — replace (public only)
            if (structSources.ContainsKey(name))
            {
                // Lang-16: require explicit override keyword for any cross-file replacement
                if (!s.IsOverride)
                {
                    string existingFrom = structSources[name];
                    _errors.Add($"[{origin}] Struct '{name}' conflicts with declaration from '{existingFrom}'. Use 'override struct' to intentionally replace it, or 'private struct' to keep both independently");
                    return;
                }

                for (int j = 0; j < target.Structs.Count; j++)
                {
                    if (target.Structs[j].Name == name && !target.Structs[j].IsPrivate)
                    {
                        target.Structs[j] = s;
                        break;
                    }
                }
            }
            else
            {
                // Lang-16: override on a new declaration (nothing to override) → error
                // Guard on isMainFile: resolved child modules propagate IsOverride through include chains,
                // but the "no prior declaration" check only applies to the file's own declarations.
                if (isMainFile && s.IsOverride)
                {
                    _errors.Add($"[{origin}] 'override' on struct '{name}' but no prior declaration exists to override");
                    return;
                }
                target.Structs.Add(s);
            }

            structSources[name] = origin;
        }

        private void MergeEnum(
            ModuleNode target, EnumDecl e,
            Dictionary<string, string> enumSources,
            string srcFile,
            bool isMainFile)
        {
            // Lang-15: stamp OriginFile (preserve original if already set — diamond include)
            if (e.OriginFile == null) e.OriginFile = srcFile;
            string origin = e.OriginFile;

            string name = e.Name;

            // Lang-15: private enums never conflict with enums from other files.
            if (e.IsPrivate)
            {
                string qualKey = name + "\0" + origin;
                if (enumSources.ContainsKey(qualKey))
                {
                    _errors.Add($"[{origin}] Duplicate private enum '{name}' in the same file");
                    return;
                }
                target.Enums.Add(e);
                enumSources[qualKey] = origin;
                return;
            }

            // Check same-file redefinition vs diamond include
            string existingFile;
            if (enumSources.TryGetValue(name, out existingFile) && existingFile == origin)
            {
                if (isMainFile)
                {
                    _errors.Add($"[{origin}] Duplicate enum '{name}' in the same file");
                    return;
                }
                // Diamond include → skip silently
                return;
            }

            // Cross-file override or new declaration — replace (public only)
            if (enumSources.ContainsKey(name))
            {
                // Lang-16: require explicit override keyword for any cross-file replacement
                if (!e.IsOverride)
                {
                    string existingFrom = enumSources[name];
                    _errors.Add($"[{origin}] Enum '{name}' conflicts with declaration from '{existingFrom}'. Use 'override enum' to intentionally replace it, or 'private enum' to keep both independently");
                    return;
                }

                for (int j = 0; j < target.Enums.Count; j++)
                {
                    if (target.Enums[j].Name == name && !target.Enums[j].IsPrivate)
                    {
                        target.Enums[j] = e;
                        break;
                    }
                }
            }
            else
            {
                // Lang-16: override on a new declaration (nothing to override) → error
                // Guard on isMainFile: resolved child modules propagate IsOverride through include chains,
                // but the "no prior declaration" check only applies to the file's own declarations.
                if (isMainFile && e.IsOverride)
                {
                    _errors.Add($"[{origin}] 'override' on enum '{name}' but no prior declaration exists to override");
                    return;
                }
                target.Enums.Add(e);
            }

            enumSources[name] = origin;
        }

        // ===== Lang-18: Aliased override application =====

        /// <summary>
        /// Lang-18: Apply an aliased function override to the aliased module.
        /// Replaces the matching public function in the aliased module.
        /// </summary>
        private void ApplyAliasedFuncOverride(ModuleNode target, FuncDecl f, string mainFilePath)
        {
            string alias = f.AliasTarget;
            if (!target.AliasedModules.TryGetValue(alias, out var aliasModule))
            {
                _errors.Add($"[{mainFilePath}] '{alias}' is not a known include alias; cannot override '{alias}.{f.Name}'");
                return;
            }
            if (!f.IsOverride)
            {
                _errors.Add($"[{mainFilePath}] Use 'override func {alias}.{f.Name}' to replace a declaration in aliased module '{alias}'");
                return;
            }
            if (f.OriginFile == null) f.OriginFile = mainFilePath;
            bool found = false;
            for (int j = 0; j < aliasModule.Functions.Count; j++)
            {
                if (aliasModule.Functions[j].Name == f.Name && !aliasModule.Functions[j].IsPrivate)
                {
                    aliasModule.Functions[j] = f;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                _errors.Add($"[{mainFilePath}] 'override' on function '{alias}.{f.Name}' but no public function '{f.Name}' exists in aliased module '{alias}'");
            }
        }

        /// <summary>
        /// Lang-18: Apply an aliased variable/const override to the aliased module.
        /// Replaces the matching public var/const in the aliased module.
        /// </summary>
        private void ApplyAliasedVarOverride(ModuleNode target, VarDeclStmt v, string mainFilePath)
        {
            string alias = v.AliasTarget;
            if (!target.AliasedModules.TryGetValue(alias, out var aliasModule))
            {
                _errors.Add($"[{mainFilePath}] '{alias}' is not a known include alias; cannot override '{alias}.{v.Name}'");
                return;
            }
            if (!v.IsOverride)
            {
                string kind = v.IsConst ? "const" : "var";
                _errors.Add($"[{mainFilePath}] Use 'override {kind} {alias}.{v.Name}' to replace a declaration in aliased module '{alias}'");
                return;
            }
            if (v.OriginFile == null) v.OriginFile = mainFilePath;
            bool found = false;
            for (int j = 0; j < aliasModule.ModuleVariables.Count; j++)
            {
                var existing = aliasModule.ModuleVariables[j];
                if (existing.Name == v.Name && !existing.IsPrivate && existing.IsConst == v.IsConst)
                {
                    aliasModule.ModuleVariables[j] = v;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                string kind = v.IsConst ? "const" : "var";
                _errors.Add($"[{mainFilePath}] 'override' on {kind} '{alias}.{v.Name}' but no public {kind} '{v.Name}' exists in aliased module '{alias}'");
            }
        }

        /// <summary>
        /// Lang-18: Apply an aliased struct override to the aliased module.
        /// Replaces the matching public struct in the aliased module.
        /// </summary>
        private void ApplyAliasedStructOverride(ModuleNode target, StructDecl s, string mainFilePath)
        {
            string alias = s.AliasTarget;
            if (!target.AliasedModules.TryGetValue(alias, out var aliasModule))
            {
                _errors.Add($"[{mainFilePath}] '{alias}' is not a known include alias; cannot override '{alias}.{s.Name}'");
                return;
            }
            if (!s.IsOverride)
            {
                _errors.Add($"[{mainFilePath}] Use 'override struct {alias}.{s.Name}' to replace a declaration in aliased module '{alias}'");
                return;
            }
            if (s.OriginFile == null) s.OriginFile = mainFilePath;
            bool found = false;
            for (int j = 0; j < aliasModule.Structs.Count; j++)
            {
                if (aliasModule.Structs[j].Name == s.Name && !aliasModule.Structs[j].IsPrivate)
                {
                    aliasModule.Structs[j] = s;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                _errors.Add($"[{mainFilePath}] 'override' on struct '{alias}.{s.Name}' but no public struct '{s.Name}' exists in aliased module '{alias}'");
            }
        }

        /// <summary>
        /// Lang-18: Apply an aliased enum override to the aliased module.
        /// Replaces the matching public enum in the aliased module.
        /// </summary>
        private void ApplyAliasedEnumOverride(ModuleNode target, EnumDecl e, string mainFilePath)
        {
            string alias = e.AliasTarget;
            if (!target.AliasedModules.TryGetValue(alias, out var aliasModule))
            {
                _errors.Add($"[{mainFilePath}] '{alias}' is not a known include alias; cannot override '{alias}.{e.Name}'");
                return;
            }
            if (!e.IsOverride)
            {
                _errors.Add($"[{mainFilePath}] Use 'override enum {alias}.{e.Name}' to replace a declaration in aliased module '{alias}'");
                return;
            }
            if (e.OriginFile == null) e.OriginFile = mainFilePath;
            bool found = false;
            for (int j = 0; j < aliasModule.Enums.Count; j++)
            {
                if (aliasModule.Enums[j].Name == e.Name && !aliasModule.Enums[j].IsPrivate)
                {
                    aliasModule.Enums[j] = e;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                _errors.Add($"[{mainFilePath}] 'override' on enum '{alias}.{e.Name}' but no public enum '{e.Name}' exists in aliased module '{alias}'");
            }
        }
    }
}
