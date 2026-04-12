# 易用性补充：语法信息来源范围 × 编译方式

> **状态**：✅ 已完成讨论 → VM_Summary.md §七 Lang 串行计划（DX4 系列 ✅ 全部完成；DX5 ✅ 已完成）
> **来源**：DX4 — 模块化编译与宿主集成易用性
> **日期**：2026-04-11

---

## 一、背景

随着 Lang-15（public/private 可见性）、Lang-16（override）、Lang-17（include as 别名）、Lang-18（override alias）相继完成，FFVM 已具备完整的多文件模块化能力。但在实际业务场景中，**语法信息来源的范围配置**和**编译方式的选择**仍缺乏系统性的易用性支撑。

本文讨论如何让"多模块组合 + 宿主能力声明 + 编译方式选择"三者在工具链（编译器 + LSP）层面更加顺畅。

---

## 二、语法信息来源范围

### 2.1 定义

先定义几个语法信息来源范围，彼此正交：

| 模块 | 性质 | 说明 |
|------|------|------|
| **Game 模块** | 静态 | 游戏核心逻辑声明（公共 struct/enum/const/func） |
| **AI 模块** | 静态 | AI 行为逻辑声明 |
| **Skill 模块** | 静态 | 技能逻辑声明 |
| **表现模块** | 静态 | 特效/声音/动画控制声明 |
| **特殊支持模块** | 静态 | 调试辅助、性能度量等工具声明 |
| **Host 模块** | **动态** | 宿主相关能力（Syscall + ServiceBinding） |

### 2.2 Host 模块的特殊性

Host 模块是唯一的**动态**来源。其能力集合取决于宿主环境，可能通过以下途径指定：

| 途径 | 说明 | 现有机制 |
|------|------|----------|
| **编译期注入** | 宿主 C# 代码传递 `syscalls` Dict + `ServiceBinding[]` | ✅ `BytecodeCompiler.Compile()` |
| **声明文件** | `.ffvm.d.json` 描述 syscall 签名元数据 | ✅ `LspServer.LoadDeclarationJson()` |
| **运行时动态** | 宿主在运行时注册/注销 syscall | ✅ `SyscallTable` 动态注册 |
| **缺失** | 独立编译时无宿主信息 | 当前行为：未知 syscall 报编译错误 |
| **配置文件指定** | 项目描述文件指向 Host 声明 | ❌ 未实现 |

### 2.3 FFVM 现有机制映射

```
语法信息来源              → FFVM 机制层
──────────────────────────────────────────
Game/AI/Skill/表现/特殊   → include / include-as（静态 .ffs 文件）
Host 模块                 → syscalls + ServiceBinding + .ffvm.d.json
```

---

## 三、典型使用场景

### 场景 1：单文件

```ffs
// 无任何外置信息，所有声明自包含
func main() { ... }
```

- 现有支持：✅ 完整支持

### 场景 2：Game + 宿主(技能控制系统) + Skill

```ffs
include "game/core"
include "skill/base" as Skill

// 宿主提供 syscall: PlayAnimation, ApplyDamage, SpawnEffect ...
func main() {
    var phase: Skill.HitPhase = Skill.HitPhase { ... }
    PlayAnimation(1)
    ...
}
```

- 静态层：✅ include / include-as 已支持
- 宿主层：需要 syscall 声明（编译期注入或 .ffvm.d.json）

### 场景 3：Game + 宿主(AI控制系统) + Skill

```ffs
include "game/core"
include "skill/base" as Skill

// 宿主提供不同的 syscall 集合: GetTarget, EvaluateThreat, SetBehavior ...
func main() {
    var target: int = GetTarget()
    ...
}
```

- 与场景 2 结构相同，但 Host 能力集不同
- **痛点**：如何让 LSP 知道当前脚本对应哪个 Host 配置？

### 场景 4：Game + 特效 + 声音 + 表现控制

```ffs
include "game/core"
include "vfx/particles" as VFX
include "sfx/audio" as SFX
include "presentation/camera" as Cam

func main() {
    VFX.Emit(...)
    SFX.Play(...)
    Cam.Shake(...)
}
```

- 多层静态模块组合 + 宿主表现 API
- 现有支持：✅ include-as 命名空间隔离

---

## 四、编译方式

### 4.1 整体联合编译

```
入口 .ffs → Preprocessor 展开全部 include → 合并 ModuleNode → BytecodeCompiler → VMProgram
```

- **特点**：一次编译，一个 VMProgram，所有符号在同一编译单元可见
- **适用**：单实例脚本、小规模模块组合
- **现有支持**：✅ 完整支持（include 链 + override + private/public）

### 4.2 分块编译

```
模块A.ffs → 编译 → VMProgram_A (含 ExportTable)
模块B.ffs → 编译 → VMProgram_B (含 ExportTable)
入口.ffs  → 编译 (ServiceBinding[A, B]) → VMProgram_Entry
                                          ↓
运行时: VMWorld 加载 3 个 VMProgram，XCALL 跨实例调用
```

- **特点**：各模块独立编译，通过 ExportTable + ServiceBinding 连接
- **适用**：大规模系统、需要热更新单个模块、跨团队协作
- **现有支持**：✅ XCALL + ServiceBinding + 跨模块 inline（Lang-6~Lang-9）

### 4.3 对比

| 维度 | 整体联合编译 | 分块编译 (XCALL) |
|------|-------------|-----------------|
| include 链深度 | 可能很深 | 每块浅 |
| 符号可见性 | 全局可见 (public) | 仅 @export 可见 |
| override 能力 | ✅ 可覆盖 include 声明 | ❌ ExportTable 固定 |
| 热更新粒度 | 整体重编译 | 单模块重编译 |
| 跨模块 inline | ✅ include 内自动 inline | ✅ ServiceBinding.InlineInfo |
| 编译速度 | O(所有文件) | O(变更文件) |

### 4.4 推荐混合策略

- **模块内**用整体联合编译（Game + Skill 在同一编译单元）
- **模块间**用分块编译（技能控制系统 vs AI 控制系统各自独立编译）
- **宿主能力**通过声明文件统一描述，编译器和 LSP 共用同一份声明

---

## 五、跨语言参考

### 5.1 对比矩阵

| 维度 | C/C++ | Lua | GLSL/HLSL | Unreal BP | GDScript | Haxe | **FFVM** |
|------|-------|-----|-----------|-----------|----------|------|----------|
| **静态模块** | `#include` 文本展开 | `require` 返回 table | `#include` 文本展开 | C++ UCLASS 反射 | `preload`/`class_name` | `import` 包级 | `include`/`include as` |
| **宿主能力** | `extern`/FFI | `lua_pushcfunction` | 驱动内建 | UFUNCTION 反射 | `ClassDB::bind_method` | `@:native` extern | `syscall` 注册 |
| **宿主声明** | `.h` 头文件 | EmmyLua `.d.tl` | 无(内建) | `.generated.h` | GDExtension | `.hx` extern class | `.ffvm.d.json` |
| **模块组合** | 头文件统一 | require + C 绑定 | 统一 built-in | 统一反射 | 统一 ClassDB | 统一 import | **include + syscall 两套** |
| **编译单元** | translation unit | 文件 = module | shader program | per-class | 每个 .gd | 每个 .hx | 入口 + include 链 |
| **分块编译** | ✅ 单元+链接 | ✅ require 懒加载 | ✅ VS/PS 分别 | ✅ per-class | ✅ per-file 增量 | ✅ per-module | ⚠️ 仅 XCALL |
| **搜索路径** | `-I` flag | `package.path` | `-I` flag | .uproject | project.godot | build.hxml | **❌ 无** |

### 5.2 最相关的参考语言

#### Lua (require + C 绑定) — ★★★★★ 最相似

| 要素 | Lua | FFVM |
|------|-----|------|
| 脚本间引用 | `require("module")` → table | `include "module"` → mixin/alias |
| 宿主函数 | `lua_pushcfunction(L, fn)` | `syscalls["fn"] = slot` |
| 宿主类型信息 | 运行时无; `.d.tl` 补充给 IDE | `.ffvm.d.json` 补充给 LSP |
| 模块组合 | `package.path` 搜索路径 | include 路径 (相对) |
| 分块编译 | 每个文件独立为 bytecode | 每个入口 + include 链 → VMProgram |

**关键差异**：Lua require 是**运行时**加载；FFVM include 是**编译时**文本展开。FFVM 更接近 C 的编译模型。

#### GLSL/HLSL (Shader 编译) — ★★★★ 很相似

| 要素 | Shader | FFVM |
|------|--------|------|
| 静态 include | `#include "common.hlsli"` | `include "common.ffs"` |
| 宿主能力 | 驱动内建 (采样器/矩阵) | syscall (宿主注册) |
| 编译组合 | VS+PS 分别编译，共享 include | 多脚本共享同一 include |
| 变体系统 | `#define` + `#ifdef` | `override` 覆盖声明 |

**洞察**：Shader 的"变体系统"对应 FFVM 的 `override` 机制 — 同一 base include，不同脚本覆盖不同参数。

#### TypeScript / tsconfig.json — ★★★ 项目配置参考

| 要素 | TypeScript | FFVM |
|------|-----------|------|
| 项目描述 | `tsconfig.json` | ❌ 无 |
| 搜索路径 | `compilerOptions.paths` | ❌ 无 |
| 声明文件 | `.d.ts` | `.ffvm.d.json` |
| 入口配置 | `include` / `files` | ❌ 无 |

### 5.3 共性 Pattern 提炼

| Pattern | 典型代表 | FFVM 对应 |
|---------|---------|-----------|
| **P1: 声明文件** — 宿主能力描述为目标语言可理解的声明 | `.h`(C), `.d.tl`(Lua), `.d.ts`(TS) | `.ffvm.d.json` (✅ 仅 syscall) |
| **P2: 搜索路径** — 路径配置控制模块可见范围 | `-I`(C), `package.path`(Lua), `paths`(TS) | ❌ 无 |
| **P3: 入口文件** — 根文件显式 include 需要的模块 | `main.c`(C), `init.lua`(Lua) | 脚本 include 链 (✅) |
| **P4: 项目描述** — 统一配置编译参数+搜索路径+声明文件 | `tsconfig.json`, `build.hxml` | ❌ 无 |

---

## 六、推荐方案：项目描述文件 `.ffproj`

### 6.1 设计

统一 Layer 1（静态模块搜索路径）+ Layer 2（宿主声明文件）的配置。对标 `tsconfig.json`(TS) / `.luarc.json`(Lua) / `build.hxml`(Haxe)。

```json
{
  "includePaths": ["modules/game", "modules/skill"],
  "hostDeclarations": ["host/skill_system.ffvm.d.json"],
  "entry": "scripts/skill_ctrl.ffs",
  "compileOptions": {
    "inlineThreshold": 16,
    "diagnosticsEnabled": true
  }
}
```

### 6.2 场景映射

```json
// 场景 2: Game + 宿主(技能控制系统) + Skill
{
  "includePaths": ["modules/game", "modules/skill"],
  "hostDeclarations": ["host/skill_system.ffvm.d.json"],
  "entry": "scripts/skill_ctrl.ffs"
}

// 场景 3: Game + 宿主(AI控制系统) + Skill
{
  "includePaths": ["modules/game", "modules/skill"],
  "hostDeclarations": ["host/ai_system.ffvm.d.json"],
  "entry": "scripts/ai_ctrl.ffs"
}

// 场景 4: Game + 特效 + 声音 + 表现
{
  "includePaths": ["modules/game", "modules/vfx", "modules/sfx", "modules/presentation"],
  "hostDeclarations": ["host/render_system.ffvm.d.json"],
  "entry": "scripts/presentation_ctrl.ffs"
}

// 场景 1: 单文件
{
  "entry": "scripts/standalone.ffs"
}
```

### 6.3 对 LSP 的影响

| 场景 | 无 .ffproj | 有 .ffproj |
|------|-----------|-----------|
| include 路径解析 | LSP 不知道去哪找文件 | `includePaths` 告知搜索路径 |
| 宿主 syscall 补全 | 需手动加载 .ffvm.d.json | `hostDeclarations` 自动加载 |
| 入口函数名 | 硬编码 "entry" | `entry` 字段可推断 |
| 编译选项 | 默认值 | `compileOptions` 一致 |

### 6.4 LSP 发现流程

```
1. initialize(rootUri) → 扫描 rootUri 下 *.ffproj
2. 为每个 .ffproj 构建:
   a. IFileResolver (基于 includePaths + rootUri)
   b. syscalls + signatures (基于 hostDeclarations)
3. didOpen/didChange(uri) → 找到 uri 所属 .ffproj
   a. 用对应的 FileResolver + syscalls 编译
   b. 缓存合并后 AST → 跨文件符号查询
```

### 6.5 `.ffproj` 自动创建途径

用户不一定知道 `.ffproj` 的格式和配置项，因此需要自动创建途径。参考其他语言的做法：

| 语言 | 项目文件 | 自动创建途径 |
|------|---------|-------------|
| TypeScript | `tsconfig.json` | `tsc --init` 生成带注释的模板 |
| Rust | `Cargo.toml` | `cargo init` / `cargo new` 脚手架 |
| Go | `go.mod` | `go mod init` |
| Lua (LSP) | `.luarc.json` | 手写或 IDE 提示生成 |
| Haxe | `build.hxml` | 手写（格式极简，一行一参数） |
| Godot | `project.godot` | 编辑器 GUI 自动生成 |

FFVM 推荐三层递进：

**Layer 0：零配置（DX4-P0 即可）**
- 大多数小项目**不需要** `.ffproj`
- LSP 用 rootUri 作为搜索根 + 自动发现 workspace 内的 `.ffvm.d.json`
- 单文件 / 单入口 + 少量 include 的场景完全够用

**Layer 1：CLI 脚手架命令（DX4-P2 一起实现）**

```bash
ffvm-cli init                    # 在当前目录生成最小 .ffproj
ffvm-cli init --host skill       # 生成 + 预填技能系统 host 声明路径
```

生成内容（带注释的模板）：

```json
{
  // include 搜索路径（相对于 .ffproj 所在目录）
  "includePaths": ["."],
  // 宿主声明文件（syscall 签名 + service 定义）
  "hostDeclarations": [],
  // 入口脚本（可选，用于 compile 命令）
  "entry": null,
  // 编译选项（可选，覆盖默认值）
  "compileOptions": {}
}
```

**Layer 2：LSP 辅助创建**
- LSP 启动时发现 workspace 内有 `.ffs` 文件但无 `.ffproj` → 通过 `window/showMessageRequest` 提示：
  > "检测到 FFScript 文件但无项目配置。是否创建 .ffproj？"
  > [创建] [忽略] [不再提示]
- 点击"创建"后 LSP 用 `workspace/applyEdit` 生成模板文件

> **决定**：Layer 1（CLI `init`）纳入 DX4-P2 一起实现。Layer 2（LSP 提示）视 DX4-P2 完成后再评估。

---

## 七、不推荐的方案与理由

| 方案 | 为什么不推荐 |
|------|------------|
| **隐式全扫描**（扫描 workspace 内所有 .ffs） | 无法区分哪些文件属于同一编译单元 |
| **纯 include 链推断** | 无法解决 Host 声明问题，也无法确定搜索根路径 |
| **在 .ffs 中内嵌配置** | 违反关注点分离；同一 .ffs 可能被多个项目组合使用 |
| **C 风格 Makefile** | 过度工程化；FFS 项目规模不需要构建系统复杂度 |

---

## 八、实施分解

基于以上讨论，建议拆分为以下子需求（DX4 系列），纳入 Lang 串行计划：

| 子需求 | 内容 | 前置 | 复杂度 |
|--------|------|------|--------|
| **DX4-P0** | LSP workspace 快速改善：rootUri 磁盘 FileResolver + 自动发现 .ffvm.d.json | — | ⭐⭐ |
| **DX4-P1** | `.ffproj` 项目描述文件：格式定义 + 解析 + LSP 加载 + includePaths + hostDeclarations | DX4-P0 | ⭐⭐⭐ |
| **DX4-P2** | CLI 集成：`ffvm-cli init` 脚手架生成 `.ffproj` 模板 + `ffvm-cli compile --project x.ffproj` 读取项目配置编译 | DX4-P1 | ⭐⭐ |
| **DX4-P3** | 跨文件符号查询：合并 AST 缓存 + 跨文件 definition/references/hover + OriginFile→URI 映射 | DX4-P1 | ⭐⭐⭐ |
| **DX4-P4** | LSP 辅助创建 `.ffproj`：workspace 内有 `.ffs` 无 `.ffproj` → `window/showMessageRequest` 提示 → `workspace/applyEdit` 生成模板（§6.5 Layer 2） | DX4-P2 | ⭐⭐ |

---

## 九、待确认

1. **`.ffproj` 名字和格式**：JSON？命名为 `.ffproj` / `.ffs.json` / `ffvm.config.json`？
2. **DX4-P0 是否足够**：小项目可能只需 rootUri + 自动发现，无需 .ffproj？
3. **分块编译边界**：由 .ffproj 配置还是由 include/ServiceBinding 语法自然区分？
4. **宿主声明生成**：.ffvm.d.json 是手写还是从宿主代码自动生成？
5. ~~**`.ffproj` 自动创建**：用户不知道格式怎么办？~~ → ✅ 已确认：CLI `ffvm-cli init` 脚手架（§6.5），纳入 DX4-P2。

---

## 十、结论

**所有点已锁定**。

- 语法信息来源分为静态模块（include/include-as）和动态宿主（syscall/ServiceBinding/.ffvm.d.json）两层
- 编译方式为整体联合编译（同模块内）+ 分块编译（跨模块间）混合策略
- 跨语言对比确认 `.ffproj` 项目描述文件是最符合 FFVM 定位的易用性补充方案
- 实施拆分为 DX4-P0 ~ DX4-P4 五个子需求，纳入 Lang 串行计划

---

## 十一、后续补充功能（DX5 已实现）

> **状态**：✅ 已完成 — DX5 系列（2026-04-12）

DX4 系列完成后，DX5 补充了以下 LSP 易用性功能：

| 功能 | 实现方式 | 对应 DX5 测试 |
|------|---------|--------------|
| **LSP 重命名**（F2） | `textDocument/rename` + `prepareRename`，函数/变量/结构体/枚举跨引用 WorkspaceEdit | DX5-06~08, 18~19 |
| **语义染色** | `textDocument/semanticTokens/full`，struct/enum/field/member 声明处 token | DX5-03~05, 20 |
| **Include 路径导航** | `textDocument/definition` 点击 include 路径 → 跳转目标文件；`textDocument/references` 查找同路径 include | DX5-12~14 |
| **结构体字段 / 枚举成员** | `SymbolKindTag.StructField`/`EnumMember` + definition/references | DX5-09~11 |
| **`.ffproj` 文件夹名** | 自动创建使用工作区文件夹名（`MyProject.ffproj`） | DX5-15 |
| **GenerateTemplate 带注释** | 模板含中英双语 `//` 注释 + `StripLineComments` 解析 | DX5-16~17 |
| **TextMate grammar 增强** | struct/enum 声明 + 类型注解正则染色（LSP semantic tokens 降级方案） | — |

**遗留项（已提交串行计划）**：

| 编号 | 遗留项 | 根本原因 | 串行计划编号 |
|------|--------|---------|-------------|
| R1 | Include 文件重命名时自动更新所有 `include` 引用 | LSP rename 无法操作文件系统；需 `workspace/willRenameFiles` | **DX6** ✅ |
| R2 | 结构体字段使用处（`v.x`）精确 references/rename | `FieldAccessExpr` 缺少 `FieldNameLine`/`FieldNameColumn` | **DX7** ✅ |
| R3 | 类型注解使用处（`var v: Vec2`）精确语义染色 | `VarDeclStmt`/`ParamDecl` 缺少 `TypeNameLine`/`TypeNameColumn` | **DX7** ✅ |
