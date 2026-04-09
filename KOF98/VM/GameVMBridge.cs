using System;
using System.Collections.Generic;
using System.IO;
using FFVM;
using FFVM.Compiler;

namespace KOF98
{
    /// <summary>
    /// Filesystem-based file resolver for include directives.
    /// Resolves include paths relative to a base directory (typically KOF98/Scripts/).
    /// Paths are validated to stay within the base directory (no path traversal).
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
            string fullPath = Path.GetFullPath(Path.Combine(_baseDir, path));
            // Append .ffs extension if not present (include "common/constants" → common/constants.ffs)
            if (!fullPath.EndsWith(".ffs", StringComparison.OrdinalIgnoreCase))
                fullPath += ".ffs";
            // Validate path stays within base directory (prevent path traversal)
            if (!fullPath.StartsWith(_baseDir, StringComparison.OrdinalIgnoreCase))
                return null;
            if (!File.Exists(fullPath)) return null;
            return File.ReadAllText(fullPath);
        }
    }

    /// <summary>
    /// Bridge between the KOF98 game framework and FFVM.
    ///
    /// Responsibilities:
    /// - Owns the VMWorld instance
    /// - Manages VM instance ↔ game entity mapping
    /// - Compiles .ffs scripts and loads them as VM modules
    /// - Drives VM execution each frame (Tick)
    /// - Handles skill activation → VM instance spawn
    /// - Handles skill deactivation → VM instance kill
    ///
    /// VM Application Points (where FFVM instances are used):
    /// 1. Skill execution: each active skill → 1 VM instance
    /// 2. AI: each AI-controlled character → 1 VM instance (future)
    /// 3. Persistent effects: DOT/buff → 1 VM instance (future)
    /// 4. Projectile behavior: complex projectiles → 1 VM instance (future)
    /// </summary>
    public class GameVMBridge
    {
        private readonly GameScene _scene;
        public VMWorld World { get; }

        /// <summary>
        /// File resolver for include directives. Resolves paths relative to the scripts base directory.
        /// </summary>
        private readonly IFileResolver _fileResolver;

        /// <summary>
        /// Maps VM instance ID → owning character ID.
        /// Used by syscalls to resolve "self" references.
        /// </summary>
        private readonly Dictionary<int, int> _instanceToOwner = new();

        /// <summary>
        /// Maps (charId, skillDefId) → VM instance ID.
        /// Used to look up active skill VM instances.
        /// </summary>
        private readonly Dictionary<(int charId, int skillId), int> _skillToInstance = new();

        /// <summary>Next available module slot for auto-loading scripts.</summary>
        private int _nextModuleSlot;

        public GameVMBridge(GameScene scene, string scriptsBaseDir = null)
        {
            _scene = scene;
            World = new VMWorld();
            _fileResolver = scriptsBaseDir != null ? new FileSystemFileResolver(scriptsBaseDir) : null;
            GameSyscalls.RegisterAll(World.Syscalls);
            RegisterManagementSyscalls();
        }

        // ── Module Loading ───────────────────────────────────────

        /// <summary>
        /// Compile an .ffs script and register it as a VM module.
        /// Returns the module slot index, or -1 on failure.
        /// Include directives are resolved relative to the scripts base directory.
        /// </summary>
        public int LoadScript(string scriptPath)
        {
            try
            {
                string source = File.ReadAllText(scriptPath);
                // Derive a logical file path for include cycle detection.
                // If we have a file resolver, compute the relative path from the base dir;
                // otherwise use the raw script path.
                string logicalPath = Path.GetFileName(scriptPath);
                return CompileAndLoad(source, logicalPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[VMBridge] Failed to load script {scriptPath}: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Compile source code and register as a VM module.
        /// Returns the module slot index.
        /// When a file resolver is configured (scriptsBaseDir passed to constructor),
        /// include directives in scripts are resolved automatically.
        /// </summary>
        public int CompileAndLoad(string source, string name = "inline")
        {
            var syscallMap = GameSyscalls.GetSyscallMap();
            var compiler = new BytecodeCompiler();
            var result = compiler.Compile(source, "main", syscallMap, World.Syscalls,
                _fileResolver, name);

            if (!result.Success)
            {
                Console.Error.WriteLine($"[VMBridge] Compilation failed for {name}");
                if (result.Errors != null)
                    foreach (var err in result.Errors)
                        Console.Error.WriteLine($"  {err}");
                return -1;
            }

            int slot = _nextModuleSlot++;
            World.Modules.Load(slot, result.Program);
            return slot;
        }

        // ── Skill Activation / Deactivation ──────────────────────

        /// <summary>
        /// Spawn a VM instance for a skill activation.
        /// Call when a character activates a VM-driven skill.
        /// </summary>
        public int ActivateSkillVM(int charId, SkillInstance skill)
        {
            if (skill.Def.VMModuleSlot < 0) return -1;

            int vmId = World.SpawnInstance(skill.Def.VMModuleSlot, 0);
            if (vmId < 0)
            {
                Console.Error.WriteLine($"[VMBridge] VM pool exhausted for char {charId} skill {skill.Def.Name}");
                return -1;
            }

            skill.VMInstanceId = vmId;
            _instanceToOwner[vmId] = charId;
            _skillToInstance[(charId, skill.Def.Id)] = vmId;

            return vmId;
        }

        /// <summary>
        /// Probe a VM-driven skill's activation condition (SK7 / T2-4).
        ///
        /// Spawns a VM instance, sets context, executes its first tick:
        ///   - If the script returns/completes on the first frame → condition FAILED (returns false).
        ///   - If the script yields/continues → condition PASSED (returns true).
        ///
        /// On failure: the instance is killed and cleaned up.
        /// On success: the instance remains alive and its ID is stored in the out parameter
        /// for the caller to attach to a SkillInstance.
        /// </summary>
        public bool ProbeSkillCondition(int charId, SkillDef def, out int vmInstanceId)
        {
            vmInstanceId = -1;
            if (def.VMModuleSlot < 0) return false;

            int vmId = World.SpawnInstance(def.VMModuleSlot, 0);
            if (vmId < 0) return false;

            _instanceToOwner[vmId] = charId;

            // Set context so syscalls can resolve "self"
            GameSyscalls.SetContext(_scene);
            GameSyscalls.VMBridge = this;

            // Execute the first tick — the script's condition check runs here
            World.TickInstance(vmId);

            GameSyscalls.VMBridge = null;

            // Check if the instance completed (returned) on first tick
            ref var inst = ref World.Pool.Instances[vmId];
            if ((inst.StateFlags & VMStateFlags.Completed) != 0 || !inst.IsAlive)
            {
                // Condition failed — clean up
                _instanceToOwner.Remove(vmId);
                return false;
            }

            // Condition passed — instance is alive and yielded
            vmInstanceId = vmId;
            return true;
        }

        /// <summary>
        /// Kill the VM instance for a deactivating skill.
        /// The VM will execute its defer/cleanup blocks before termination.
        /// </summary>
        public void DeactivateSkillVM(int charId, SkillInstance skill)
        {
            if (skill.VMInstanceId < 0) return;

            int vmId = skill.VMInstanceId;
            ref var inst = ref World.Pool.Instances[vmId];
            if (inst.IsAlive && (inst.StateFlags & VMStateFlags.Completed) == 0)
            {
                inst.StateFlags |= VMStateFlags.Killed;
            }

            _instanceToOwner.Remove(vmId);
            _skillToInstance.Remove((charId, skill.Def.Id));
            skill.VMInstanceId = -1;
        }

        // ── Per-Frame Tick ───────────────────────────────────────

        /// <summary>
        /// Tick the VM world. Call once per frame during the "process skills" phase.
        /// </summary>
        public void TickVMWorld()
        {
            GameSyscalls.SetContext(_scene);
            GameSyscalls.VMBridge = this;

            // Use VMWorld.Tick() which handles all lifecycle (wait, kill, cleanup).
            // Syscalls resolve owner via VMBridge.GetOwnerForInstance(instanceId).
            World.Tick();

            // Post-tick: free completed instances back to the pool
            var pool = World.Pool;
            for (int i = pool.ActiveListCount - 1; i >= 0; i--)
            {
                int id = pool.ActiveList[i];
                ref var inst = ref pool.Instances[id];
                if ((inst.StateFlags & VMStateFlags.Completed) != 0)
                {
                    _instanceToOwner.Remove(id);
                    World.DestroyInstance(id);
                }
            }

            GameSyscalls.VMBridge = null;
        }

        // ── MI-2 / MI-3: SpawnScript / KillInstance ──────────────

        private void RegisterManagementSyscalls()
        {
            // MI-2: SpawnScript(moduleSlot, entryIP) → newInstanceId
            World.Syscalls.Register(GameConstants.SYS_SPAWN_SCRIPT, "SpawnScript",
                (ref VMInstanceState s) =>
                {
                    var args = new SyscallArgs(ref s);
                    int moduleSlot = args.GetInt(0);
                    int entryIP = args.GetInt(1);
                    int newId = World.SpawnInstance(moduleSlot, entryIP);

                    // Inherit owner from parent
                    if (newId >= 0 && _instanceToOwner.TryGetValue(s.InstanceId, out int ownerId))
                    {
                        _instanceToOwner[newId] = ownerId;
                    }

                    args.SetReturnInt(newId >= 0 ? newId : -1);
                });

            // MI-3: KillInstance(instanceId)
            World.Syscalls.Register(GameConstants.SYS_KILL_INSTANCE, "KillInstance",
                (ref VMInstanceState s) =>
                {
                    var args = new SyscallArgs(ref s);
                    int targetId = args.GetInt(0);
                    if (targetId >= 0 && targetId < World.Pool.Instances.Length)
                    {
                        ref var target = ref World.Pool.Instances[targetId];
                        if (target.IsAlive && (target.StateFlags & VMStateFlags.Completed) == 0)
                            target.StateFlags |= VMStateFlags.Killed;
                    }
                });
        }

        // ── Queries ──────────────────────────────────────────────

        public int GetOwnerForInstance(int vmInstanceId)
        {
            return _instanceToOwner.TryGetValue(vmInstanceId, out int ownerId) ? ownerId : -1;
        }

        /// <summary>
        /// Check if a VM skill instance has completed (script returned or was killed).
        /// Used by the host to detect when a VM-driven skill has finished.
        /// </summary>
        public bool IsSkillVMCompleted(SkillInstance skill)
        {
            if (skill == null || skill.VMInstanceId < 0) return false;
            int vmId = skill.VMInstanceId;
            if (vmId >= World.Pool.Instances.Length) return true;
            ref var inst = ref World.Pool.Instances[vmId];
            return (inst.StateFlags & VMStateFlags.Completed) != 0 || !inst.IsAlive;
        }
    }
}
