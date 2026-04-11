using System;
using System.Collections.Generic;
using System.IO;
using FFVM.Debug;

namespace FFVM.Compiler
{
    /// <summary>
    /// DX4-P1: Represents a .ffproj project description file.
    /// Provides unified configuration for compilation parameters, include search paths,
    /// and host declaration files.  Analogous to tsconfig.json (TypeScript) or .luarc.json (Lua).
    ///
    /// JSON format:
    /// <code>
    /// {
    ///   "includePaths": ["modules/game", "modules/skill"],
    ///   "hostDeclarations": ["host/skill_system.ffvm.d.json"],
    ///   "entry": "scripts/skill_ctrl.ffs",
    ///   "compileOptions": {
    ///     "inlineThreshold": 16,
    ///     "diagnosticsEnabled": true
    ///   }
    /// }
    /// </code>
    /// All fields are optional — missing fields use sensible defaults.
    /// Relative paths are resolved against the directory containing the .ffproj file.
    /// </summary>
    public class ProjectFile
    {
        /// <summary>
        /// Include search paths (relative to project directory).
        /// The file resolver will try each path in order when resolving include directives.
        /// Empty array when not specified.
        /// </summary>
        public string[] IncludePaths { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Paths to .ffvm.d.json host declaration files (relative to project directory).
        /// These files declare syscall signatures, service bindings, etc.
        /// Empty array when not specified.
        /// </summary>
        public string[] HostDeclarations { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Entry script path (relative to project directory).
        /// Used by CLI compile command; LSP uses the open document as entry.
        /// Null when not specified.
        /// </summary>
        public string Entry { get; set; }

        /// <summary>
        /// Compiler options. Null when not specified (use defaults).
        /// </summary>
        public CompileOptions CompileOptions { get; set; }

        /// <summary>
        /// Absolute path to the directory containing the .ffproj file.
        /// Set by Parse() to enable relative path resolution.
        /// </summary>
        public string ProjectDir { get; set; }

        /// <summary>
        /// Parse a .ffproj JSON string into a ProjectFile instance.
        /// </summary>
        /// <param name="json">JSON content of the .ffproj file</param>
        /// <param name="projectDir">Absolute path to the directory containing the .ffproj file (for relative path resolution)</param>
        /// <returns>Parsed ProjectFile, or null if json is empty/invalid</returns>
        public static ProjectFile Parse(string json, string projectDir)
        {
            if (string.IsNullOrEmpty(json)) return null;

            var root = JsonObject.Parse(json);
            if (root == null) return null;

            var pf = new ProjectFile();
            pf.ProjectDir = projectDir;

            // includePaths
            var incPaths = root.GetArray("includePaths");
            if (incPaths != null && incPaths.Count > 0)
            {
                var paths = new List<string>();
                foreach (var item in incPaths)
                {
                    string s = item as string;
                    if (!string.IsNullOrEmpty(s))
                        paths.Add(s);
                }
                pf.IncludePaths = paths.ToArray();
            }

            // hostDeclarations
            var hostDecls = root.GetArray("hostDeclarations");
            if (hostDecls != null && hostDecls.Count > 0)
            {
                var decls = new List<string>();
                foreach (var item in hostDecls)
                {
                    string s = item as string;
                    if (!string.IsNullOrEmpty(s))
                        decls.Add(s);
                }
                pf.HostDeclarations = decls.ToArray();
            }

            // entry
            pf.Entry = root.GetString("entry");

            // compileOptions
            var opts = root.GetObject("compileOptions");
            if (opts != null)
            {
                var co = new CompileOptions();
                if (opts.ContainsKey("inlineThreshold"))
                    co.InlineThreshold = opts.GetInt("inlineThreshold", co.InlineThreshold);
                if (opts.ContainsKey("inlineDepthMax"))
                    co.InlineDepthMax = opts.GetInt("inlineDepthMax", co.InlineDepthMax);
                if (opts.ContainsKey("maxHoistedPerLoop"))
                    co.MaxHoistedPerLoop = opts.GetInt("maxHoistedPerLoop", co.MaxHoistedPerLoop);
                if (opts.ContainsKey("diagnosticsEnabled"))
                    co.DiagnosticsEnabled = opts.GetBool("diagnosticsEnabled", co.DiagnosticsEnabled);
                pf.CompileOptions = co;
            }

            return pf;
        }

        /// <summary>
        /// Discover the first .ffproj file in a directory (non-recursive, top-level only).
        /// Returns null if no .ffproj is found or the directory doesn't exist.
        /// </summary>
        /// <param name="directory">Directory to scan</param>
        /// <returns>Parsed ProjectFile, or null if not found</returns>
        public static ProjectFile TryDiscover(string directory)
        {
            try
            {
                if (!Directory.Exists(directory)) return null;
                string[] files = Directory.GetFiles(directory, "*.ffproj", SearchOption.TopDirectoryOnly);
                if (files.Length == 0) return null;

                // Use the first .ffproj found (alphabetical order from filesystem)
                string json = File.ReadAllText(files[0]);
                return Parse(json, directory);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Resolve a relative path against the project directory.
        /// Returns the absolute path if projectDir is set, otherwise returns the path as-is.
        /// </summary>
        public string ResolvePath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return relativePath;
            if (string.IsNullOrEmpty(ProjectDir)) return relativePath;
            if (Path.IsPathRooted(relativePath)) return relativePath;
            return Path.GetFullPath(Path.Combine(ProjectDir, relativePath));
        }

        /// <summary>
        /// Build a CompositeFileResolver from the project's includePaths.
        /// Falls back to a single FileSystemFileResolver rooted at projectDir if no includePaths specified.
        /// </summary>
        public IFileResolver BuildFileResolver()
        {
            if (IncludePaths.Length == 0)
            {
                // No include paths — use project directory as sole root
                return ProjectDir != null ? new FileSystemFileResolver(ProjectDir) : null;
            }

            var resolvers = new List<IFileResolver>();
            foreach (string path in IncludePaths)
            {
                string absPath = ResolvePath(path);
                if (absPath != null)
                    resolvers.Add(new FileSystemFileResolver(absPath));
            }

            if (resolvers.Count == 0) return null;
            if (resolvers.Count == 1) return resolvers[0];
            return new CompositeFileResolver(resolvers.ToArray());
        }
    }

    /// <summary>
    /// DX4-P1: File resolver that tries multiple base directories in order.
    /// Used when a .ffproj specifies multiple includePaths.
    /// Returns the first successful ReadFile result across all resolvers.
    /// </summary>
    public class CompositeFileResolver : IFileResolver
    {
        private readonly IFileResolver[] _resolvers;

        public CompositeFileResolver(IFileResolver[] resolvers)
        {
            _resolvers = resolvers ?? Array.Empty<IFileResolver>();
        }

        public string ReadFile(string path)
        {
            for (int i = 0; i < _resolvers.Length; i++)
            {
                string content = _resolvers[i].ReadFile(path);
                if (content != null) return content;
            }
            return null;
        }
    }
}
