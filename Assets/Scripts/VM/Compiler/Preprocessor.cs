using System.Collections.Generic;
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
    /// Override rules:
    ///   - Cross-file: later declaration wins (const type must match, func signature must match).
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
                    wrapped.Structs.Add(module.Structs[i]);
                }
                for (int i = 0; i < module.Functions.Count; i++)
                {
                    module.Functions[i].OriginFile = filePath;  // Lang-15
                    wrapped.Functions.Add(module.Functions[i]);
                }
                for (int i = 0; i < module.ModuleVariables.Count; i++)
                {
                    module.ModuleVariables[i].OriginFile = filePath;  // Lang-15
                    wrapped.ModuleVariables.Add(module.ModuleVariables[i]);
                }
                for (int i = 0; i < module.Enums.Count; i++)
                {
                    module.Enums[i].OriginFile = filePath;  // Lang-15
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
                string importSource = _fileResolver != null ? _fileResolver.ReadFile(importPath) : null;
                if (importSource == null)
                {
                    _errors.Add($"[{filePath}] Include file not found: \"{importPath}\"");
                    continue;
                }

                var importModule = ResolveRecursive(importSource, importPath, stack);
                if (_errors.Count > 0) continue;

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
                MergeModuleVariable(target, v, constSources, varSources, declKinds, srcFile);
            }

            // Merge funcs
            for (int i = 0; i < source.Functions.Count; i++)
            {
                var f = source.Functions[i];
                MergeFunc(target, f, funcSources, srcFile);
            }

            // Merge structs
            for (int i = 0; i < source.Structs.Count; i++)
            {
                var s = source.Structs[i];
                MergeStruct(target, s, structSources, srcFile);
            }

            // Lang-13: Merge enums with cross-file override (same semantics as structs)
            for (int i = 0; i < source.Enums.Count; i++)
            {
                var e = source.Enums[i];
                MergeEnum(target, e, enumSources, srcFile);
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
                MergeModuleVariable(target, v, constSources, varSources, declKinds, srcFile);
            }

            for (int i = 0; i < mainModule.Functions.Count; i++)
            {
                var f = mainModule.Functions[i];
                MergeFunc(target, f, funcSources, srcFile);
            }

            for (int i = 0; i < mainModule.Structs.Count; i++)
            {
                var s = mainModule.Structs[i];
                MergeStruct(target, s, structSources, srcFile);
            }

            // Lang-13: Merge enums from main file
            for (int i = 0; i < mainModule.Enums.Count; i++)
            {
                var e = mainModule.Enums[i];
                MergeEnum(target, e, enumSources, srcFile);
            }
        }

        private void MergeModuleVariable(
            ModuleNode target, VarDeclStmt v,
            Dictionary<string, string> constSources,
            Dictionary<string, string> varSources,
            Dictionary<string, bool> declKinds,
            string srcFile)
        {
            // Lang-15: stamp OriginFile
            v.OriginFile = srcFile;

            string name = v.Name;

            // Lang-15: private declarations never conflict with declarations from other files.
            // They are always added (not replaced). Same-file redefinition is still an error.
            if (v.IsPrivate)
            {
                // Check same-file redefinition for private: use qualified key
                string qualKey = name + "\0" + srcFile;
                var sources = v.IsConst ? constSources : varSources;
                string existingFile;
                if (sources.TryGetValue(qualKey, out existingFile))
                {
                    string kind = v.IsConst ? "const" : "var";
                    _errors.Add($"[{srcFile}] Duplicate private {kind} '{name}' in the same file");
                    return;
                }
                target.ModuleVariables.Add(v);
                sources[qualKey] = srcFile;
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
                    _errors.Add($"[{srcFile}] Cannot override var '{name}' with const");
                else
                    _errors.Add($"[{srcFile}] Cannot override const '{name}' with var");
                return;
            }

            // Check same-file redefinition
            string existFile;
            if (varSrc.TryGetValue(name, out existFile) && existFile == srcFile)
            {
                string kind = v.IsConst ? "const" : "var";
                _errors.Add($"[{srcFile}] Duplicate {kind} '{name}' in the same file");
                return;
            }

            // Cross-file override or new declaration — replace
            if (varSrc.ContainsKey(name))
            {
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
                target.ModuleVariables.Add(v);
            }

            varSrc[name] = srcFile;
            declKinds[name] = v.IsConst;
        }

        private void MergeFunc(
            ModuleNode target, FuncDecl f,
            Dictionary<string, string> funcSources,
            string srcFile)
        {
            // Lang-15: stamp OriginFile
            f.OriginFile = srcFile;

            string name = f.Name;

            // Lang-15: private functions never conflict with functions from other files.
            if (f.IsPrivate)
            {
                string qualKey = name + "\0" + srcFile;
                if (funcSources.ContainsKey(qualKey))
                {
                    _errors.Add($"[{srcFile}] Duplicate private function '{name}' in the same file");
                    return;
                }
                target.Functions.Add(f);
                funcSources[qualKey] = srcFile;
                return;
            }

            // Check same-file redefinition
            string existingFile;
            if (funcSources.TryGetValue(name, out existingFile) && existingFile == srcFile)
            {
                _errors.Add($"[{srcFile}] Duplicate function '{name}' in the same file");
                return;
            }

            // Cross-file override or new declaration — replace (public only)
            if (funcSources.ContainsKey(name))
            {
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
                target.Functions.Add(f);
            }

            funcSources[name] = srcFile;
        }

        private void MergeStruct(
            ModuleNode target, StructDecl s,
            Dictionary<string, string> structSources,
            string srcFile)
        {
            // Lang-15: stamp OriginFile
            s.OriginFile = srcFile;

            string name = s.Name;

            // Lang-15: private structs never conflict with structs from other files.
            if (s.IsPrivate)
            {
                string qualKey = name + "\0" + srcFile;
                if (structSources.ContainsKey(qualKey))
                {
                    _errors.Add($"[{srcFile}] Duplicate private struct '{name}' in the same file");
                    return;
                }
                target.Structs.Add(s);
                structSources[qualKey] = srcFile;
                return;
            }

            // Check same-file redefinition
            string existingFile;
            if (structSources.TryGetValue(name, out existingFile) && existingFile == srcFile)
            {
                _errors.Add($"[{srcFile}] Duplicate struct '{name}' in the same file");
                return;
            }

            // Cross-file override or new declaration — replace (public only)
            if (structSources.ContainsKey(name))
            {
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
                target.Structs.Add(s);
            }

            structSources[name] = srcFile;
        }

        private void MergeEnum(
            ModuleNode target, EnumDecl e,
            Dictionary<string, string> enumSources,
            string srcFile)
        {
            // Lang-15: stamp OriginFile
            e.OriginFile = srcFile;

            string name = e.Name;

            // Lang-15: private enums never conflict with enums from other files.
            if (e.IsPrivate)
            {
                string qualKey = name + "\0" + srcFile;
                if (enumSources.ContainsKey(qualKey))
                {
                    _errors.Add($"[{srcFile}] Duplicate private enum '{name}' in the same file");
                    return;
                }
                target.Enums.Add(e);
                enumSources[qualKey] = srcFile;
                return;
            }

            // Check same-file redefinition
            string existingFile;
            if (enumSources.TryGetValue(name, out existingFile) && existingFile == srcFile)
            {
                _errors.Add($"[{srcFile}] Duplicate enum '{name}' in the same file");
                return;
            }

            // Cross-file override or new declaration — replace (public only)
            if (enumSources.ContainsKey(name))
            {
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
                target.Enums.Add(e);
            }

            enumSources[name] = srcFile;
        }
    }
}
