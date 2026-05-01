# Intent-Bound Data Manager Contract

Status: ideal implementation contract, not a user-facing design note.
Location rule: this file lives next to the implementation it constrains. Do not move it to `Docs/` or another general documentation area.

## Purpose

This file is the source-of-truth contract for the first intent-bound definition-data manager implementation in `KOF98.Game`.
When an implementation request says “apply this file”, the resulting code should converge on the same public shape, semantics, migration order, and constraints even across separate sessions.

The implementation target is the current static-definition access pattern around `GameCatalog`, `Pool<T>`, and ECS components that store integer definition handles.
The first implementation must intentionally over-specify the contract before code exists. Do not preserve ambiguity for future patches when the contract can make the intended structure explicit now.

## Contract quality rules

This contract is judged by two properties:

1. Existence
   - All required elements must be present in this file before implementation.
   - Required elements are intent, function, and constraint.
   - If an element is missing, different sessions are expected to diverge and the contract is not idempotently applicable.

2. Association
   - For sequential implementation steps, each step must naturally and uniquely determine the next step.
   - For alternative approaches to the same requirement, equivalent ideas must collapse to one chosen implementation path, not several acceptable shapes.
   - If two plausible paths remain, this contract must choose one unless a later task explicitly reopens the choice.

Application rule: if a later implementation idea conflicts with these quality rules, change this contract first or ask for clarification. Do not silently implement a weaker interpretation.

## Problem intent

The game layer has two categories of definition-data access:

1. Contract-valid gameplay access: systems and skill behavior ask for data that must exist for the current simulation to be meaningful.
2. Contract-tolerant access: debug views, console output, partially initialized entities, stale handles, invalid ids, exploratory tooling, tests, and migration probes ask for data that may be absent.

The manager must make that distinction explicit at every lookup boundary. It must not make every lookup silently nullable, and it must not make every lookup fatal. The caller's stated intent determines the access semantics.

## Non-goals

- Do not implement a general database, asset pipeline, serializer, dependency injection container, service locator, or reflection registry.
- Do not replace ECS component arrays with object graphs, dictionaries, or per-entity heap ownership.
- Do not make static definitions mutable runtime state.
- Do not add FFVM dependencies to `KOF98.Game`.
- Do not change the existing `KOF98/` app as part of applying this contract unless a later task explicitly asks for migration.
- Do not move this contract into `Docs/`; it must remain with the constrained implementation.
- Do not introduce new package dependencies.
- Do not use this contract to redesign combat, physics, input, rendering, frame-line, or snapshot systems.

## Terms

- Definition data: authoring/static data such as characters, stats, movement definitions, skill loadouts, and skill definitions.
- Runtime data: mutable ECS world state stored in `GameWorld` component arrays.
- Handle: an integer id stored in runtime data that points to definition data.
- Definition kind: one of `Character`, `Stats`, `Movement`, `SkillLoadout`, or `Skill` in the first implementation.
- Intent: the lookup mode selected by the caller before reading data.
- Strict access: a lookup that must succeed or throw a deterministic exception.
- Optional access: a lookup that may fail because absence is part of the caller's valid state model.
- Debug access: a lookup for display, diagnostics, or tooling that must not change simulation behavior.
- Raw pool access: direct use of `GameCatalog.<Pool>[id]`, `Pool<T>.Items`, or `Pool<T>` indexing without an intent-specific helper.
- Adjacent invariant: a validation step in the same method or immediately dominating call path that proves a handle is valid before raw access.

## Required implementation shape

The first implementation must evolve `GameCatalog` in place. Do not add a separate manager type in the first implementation.

Rationale:

- Current definition ownership already lives in `GameCatalog`.
- ECS runtime components already store `GameCatalog` handles.
- A second manager type would create two plausible ownership models and break association.

Allowed helper types in the first implementation:

- `DefinitionKind` enum.
- `DefinitionLookupException` exception type.
- Small private or internal formatting helpers inside or next to `GameCatalog`.

Disallowed helper types in the first implementation:

- Independent `DefinitionManager`, `DataManager`, `Registry`, or service object that owns the pools.
- Generic result object for optional access.
- Runtime-owned definition cache.
- Snapshot-owned definition container.

`GameCatalog` remains the canonical owner of these pools:

- `Characters`
- `Stats`
- `Movements`
- `SkillLoadouts`
- `Skills`

`GameCatalog.InvalidId` remains the only canonical no-data sentinel in the first implementation.

## Required public API families

The first implementation must expose three API families with these names and return styles. Names are fixed to preserve idempotent application.

### Strict API

Strict access uses `Require*` names and throws `DefinitionLookupException` on failure.

Required methods:

- `RequireCharacter(int id, string context = null)` returns `CharacterData`.
- `RequireStats(int id, string context = null)` returns `CharacterStatsDef`.
- `RequireMovement(int id, string context = null)` returns `CharacterMovementDef`.
- `RequireSkillLoadout(int id, string context = null)` returns `CharacterSkillLoadoutDef`.
- `RequireSkill(int id, string context = null)` returns `SkillDef`.

Strict access must be used by gameplay code when the data is required by simulation invariants.
Invalid ids, missing definitions, and wrong-kind accesses must fail immediately and deterministically.

### Optional API

Optional access uses `TryGet*` names and `bool` plus `out` parameters for every definition kind.

Required methods:

- `TryGetCharacter(int id, out CharacterData value)`.
- `TryGetStats(int id, out CharacterStatsDef value)`.
- `TryGetMovement(int id, out CharacterMovementDef value)`.
- `TryGetSkillLoadout(int id, out CharacterSkillLoadoutDef value)`.
- `TryGetSkill(int id, out SkillDef value)`.

Optional access must return `false` for ordinary absence, including `InvalidId`, negative ids, ids greater than or equal to the valid count, and null reference definitions in reference-type pools.
Optional access must not throw for ordinary absence and must not log by default.

Legacy nullable methods `GetCharacter(int id)` and `GetSkill(int id)` may remain as compatibility wrappers during the first implementation, but new call sites must not use them. If kept, they must delegate to `TryGetCharacter` and `TryGetSkill` respectively.

### Debug API

Debug access uses `DebugDescribe*` names and returns strings.

Required methods:

- `DebugDescribeCharacter(int id, string context = null)`.
- `DebugDescribeStats(int id, string context = null)`.
- `DebugDescribeMovement(int id, string context = null)`.
- `DebugDescribeSkillLoadout(int id, string context = null)`.
- `DebugDescribeSkill(int id, string context = null)`.

Debug access must not mutate manager state and must not throw for invalid handles.
Debug access must be used by views, console output, diagnostics, and migration probes when a readable fallback is needed.
Debug-classified display call sites must follow these rules:

- Call the matching `DebugDescribe*` method for the displayed definition handle.
- Do not replace an invalid-handle result with legacy placeholders such as `"-"`, `"none"`, `"?"`, or an entity id string.
- UI code may add labels, prefixes, layout, colors, or truncation around the returned string.
- When the handle is invalid or missing, the returned missing-definition text must remain visible and unchanged.

## Required debug text

Debug text must be stable so display tests and future migrations do not invent incompatible placeholders.

For a valid definition:

- Character: return the character name if non-empty; otherwise `Character#{id}`.
- Skill: return the skill name if non-empty; otherwise `Skill#{id}`.
- Stats: return `Stats#{id}`.
- Movement: return `Movement#{id}`.
- SkillLoadout: return `SkillLoadout#{id}`.

For an invalid or missing definition, return exactly:

`<missing {Kind} id={id} valid=0..{maxValidId}{contextSuffix}>`

Rules:

- `{Kind}` is one of `Character`, `Stats`, `Movement`, `SkillLoadout`, `Skill`.
- `{maxValidId}` is `Count - 1` for the matching pool.
- If the pool is empty, use `valid=empty` instead of `valid=0..-1`.
- `{contextSuffix}` is empty when `context` is null or empty.
- `{contextSuffix}` is ` context={context}` when context is present.

## Required exception semantics

Strict-access errors must be deterministic and actionable. They must not depend on random data, frame timing, logging configuration, culture, or iteration order.

`DefinitionLookupException` must carry, either as properties or as a deterministic message, these fields:

- definition kind
- requested id
- valid count
- valid range or empty marker
- operation/context string when supplied by caller

The message format must be stable enough for focused tests. The first implementation should use this exact format:

`Missing {Kind} definition: id={id}, valid={validRange}, context={context}`

Rules:

- `{validRange}` is `empty` when the pool count is zero.
- `{validRange}` is `0..{Count - 1}` otherwise.
- If context is null or empty, use `context=<none>`.
- Migrated strict call sites must pass a stable context string whose runtime value is exactly `TypeName.MemberName`.
- Examples: `SkillSystem.Activate`, `SkillContext.Data`, and `GameScene.ResetRound`.
- `TypeName` is the unqualified C# type name without namespace.
- For nested types, use `OuterType.InnerType.MemberName`.
- For generic types, omit generic arity and type arguments.
- The context may be written as a string literal.
- The context may be composed from `nameof`, for example `$"{nameof(SkillSystem)}.{nameof(SkillSystem.Activate)}"`.
- `nameof(TypeName)` alone is not acceptable because it loses the operation boundary.
- `nameof(MemberName)` alone is not acceptable because it loses the owning type.
- Do not include stack traces, random ids, frame numbers, timestamps, or logging categories in the message.

## Required behavior

- Valid strict lookup returns the exact stored definition object or value from the matching pool.
- Invalid strict lookup fails immediately with `DefinitionLookupException`.
- Optional lookup returns `true` and the exact stored value for valid ids.
- Optional lookup returns `false` and `default` for invalid ids or missing reference definitions.
- Debug lookup returns stable text for both valid and invalid ids.
- Allocation returns stable integer handles for the lifetime of current manager contents.
- Existing handles must not be recycled within the same manager contents.
- Clear/reset is allowed for tests and bootstrapping, but must be explicit.
- Definition data is not part of `GameSnapshot`; snapshots store runtime handles only.
- Snapshot restore assumes the same compatible `GameCatalog` contents are available.

## Data ownership and lifecycle

- Definition data belongs to `GameCatalog`.
- Runtime ECS components only store handles and mutable state.
- Skill behavior can read definition data through context objects, but must not own global definition registries.
- Views can read through debug or optional access, not through strict gameplay-only helpers.
- Tests may reset manager contents.
- Production frame code must not clear global definitions mid-frame.
- `KOF98.CsSim` may allocate definitions into `GameCatalog`, but it must not become the owner of the registry.

## ECS constraints

- Keep `GameWorld` as dense component arrays indexed by entity slot.
- Keep `EntityId` generation checks separate from definition-handle validation.
- Do not introduce per-entity heap objects as the primary simulation state.
- Do not store live behavior instances in snapshots.
- Preserve the current separation between snapshot-friendly value components and transient caches.
- Preserve `IdentityComponent.CharacterId`, `SkillComponent.ActiveSkillId`, and `SkillComponent.PendingSkillId` as integer handles in the first implementation.

## Determinism constraints

- Applying this contract must not introduce nondeterministic definition ordering.
- Allocation order defines ids.
- Iteration over definitions must be stable and insertion ordered unless a later contract explicitly changes it.
- No random fallback data is allowed in gameplay paths.
- “Random” or undefined behavior is permitted only as a documented non-contract outside manager APIs; manager APIs themselves must be deterministic.
- Optional and debug access must not emit noisy logs for expected absence.

## Raw pool access rule

Raw pool access is forbidden by default after the first implementation.

Raw pool access may remain only when all of these are true:

1. The method is a hot path or low-level allocator where helper overhead would obscure the code.
2. The same method or an immediately dominating caller has already validated the id against the same pool.
3. A short adjacent comment states the invariant source.
4. The access does not cross definition kind boundaries.

Examples of invalid raw access after applying this contract:

- `GameCatalog.Stats[GameCatalog.Characters[id].StatsId]` without a preceding strict character lookup.
- View code using `GameCatalog.Characters[id]` to display a name.
- Skill behavior using nullable lookup for data that must exist for simulation.

## First implementation migration order

The first implementation must follow this order. Do not migrate call sites before the required API families exist.

1. Add `DefinitionKind` and `DefinitionLookupException`.
2. Add private shared validation/formatting helpers to `GameCatalog`.
3. Add all strict `Require*` methods.
4. Add all optional `TryGet*` methods.
5. Add all debug `DebugDescribe*` methods.
6. Convert legacy `GetCharacter` and `GetSkill` to delegate to `TryGet*` or leave behavior-equivalent wrappers documented as legacy optional access.
7. Migrate required gameplay call sites to strict access.
8. Migrate valid absence call sites to optional access.
9. Migrate display and diagnostic call sites to debug access.
10. Remove or annotate remaining raw pool access according to the raw pool access rule.
11. Add focused tests.
12. Run the existing relevant build and test commands.

## First implementation call-site classification

These classifications are part of the contract and remove migration ambiguity.

Strict gameplay access:

- `SkillContext.Data`, `SkillContext.Movement`, and `SkillContext.Stats`.
- `SkillActivationContext.Data`, `SkillActivationContext.Movement`, and `SkillActivationContext.Stats`.
- `SkillSystem` loadout resolution for alive character entities.
- `CombatSystem` stat resolution for valid attackers.
- `GameScene` life initialization from character stats.

Optional access:

- `SkillSystem` active skill lookup when `ActiveSkillId` may be `InvalidId`.
- `SkillSystem` pending or candidate skill lookup when invalid entries should be skipped.
- `SkillSystem.Activate` when a requested skill may be absent and activation should no-op.
- `GameSnapshot` behavior restoration from skill ids.
- `CharacterFactory` creation from externally supplied character ids until callers are tightened.

Debug access:

- `ConsoleGameView` character and skill display. Both character and skill text must use the exact matching `DebugDescribe*` result for the handle being displayed.
- `RaylibGameView` labels, HUD text, and diagnostic display.
  - Text labels must use `DebugDescribe*`.
  - Numeric widgets that need definition values must first use optional access, for example `TryGetCharacter` followed by `TryGetStats`.
  - Debug strings are for display text only; do not parse `DebugDescribe*` output to recover numeric data.
  - HP/power bars and other numeric definition-dependent widgets must not be rendered for that entity on that frame when required character or stats definitions are missing.
  - Do not draw empty, disabled, or default-zero bars.
  - Do not substitute placeholder numeric values.
- `KOF98_CS/Program.cs` winner display. Winner text must use `DebugDescribeCharacter`.

If a future implementation finds a call site not listed here, classify it by intent before editing it. Do not pick an API by convenience.

## Test requirements

Focused tests must cover these cases:

- Strict valid lookup returns the stored definition.
- Strict invalid lookup throws `DefinitionLookupException` with kind, id, range, and context.
- Optional lookup returns `false` and `default` for `InvalidId`.
- Optional lookup returns `false` and `default` for out-of-range ids.
- Optional lookup returns `true` for valid ids.
- Debug invalid lookup returns the exact required placeholder.
- Debug valid character and skill lookup prefer names and fall back to `Character#{id}` / `Skill#{id}`.
- Handles remain stable after multiple allocations.
- `Clear` resets counts explicitly and does not recycle handles except after explicit reset.
- `GameSnapshot` does not serialize definition data.

Tests should be focused and should not require Raylib, FFVM, or the existing `KOF98/` app.
If no persistent test project exists when this contract is applied, the first implementation must add one with these requirements:

- Path: `KOF98.Game.Tests/KOF98.Game.Tests.csproj`.
- Style: framework-free console test project.
- Framework-free means no xUnit, NUnit, MSTest, `Microsoft.NET.Test.Sdk`, or other test-framework package.
- Reference: `KOF98.Game/KOF98.Game.csproj`.
- Dependencies: no package dependencies.
- Entry point: `Program.cs` runs the focused contract checks with plain assertion helper methods.
- Failure mode: throw exceptions or return a non-zero exit code.

Required first-implementation validation commands:

- `dotnet build KOF98.Game/KOF98.Game.csproj -c Release`
- `dotnet build KOF98.CsSim/KOF98.CsSim.csproj -c Release`
- `dotnet run --project KOF98.Game.Tests/KOF98.Game.Tests.csproj -c Release`

## Implementation boundaries

Allowed first-step changes when applying this contract:

- Evolve `GameCatalog` in place with the required API families.
- Add `DefinitionKind` and `DefinitionLookupException` near `GameCatalog`.
- Add private/internal helper methods for validation and formatting.
- Update call sites according to the required classification and migration order.
- Add focused tests for strict, optional, and debug lookup semantics.
- Update directly related comments or README text only if they would otherwise contradict the new API.

Disallowed first-step changes:

- Adding an independent manager object or service layer.
- Rewriting unrelated combat, physics, input, rendering, frame-line, effect, or projectile systems.
- Migrating `KOF98/` to the CS-simulation path.
- Adding new package dependencies.
- Replacing ECS arrays with dictionaries or object ownership graphs.
- Making snapshots own or serialize definition data.
- Removing `GameCatalog.InvalidId`.
- Changing skill behavior lifecycle semantics.

## Acceptance criteria

An implementation satisfies this contract when:

- `GameCatalog` exposes all required strict, optional, and debug methods with the specified names and return styles.
- Required gameplay lookups use strict semantics or have a documented adjacent invariant that satisfies the raw pool access rule.
- Optional states such as no active skill are not represented as unexpected exceptions.
- Debug display of missing data is stable and uses the exact required placeholder format.
- Invalid required ids fail with deterministic, context-rich `DefinitionLookupException` errors.
- Definition handles remain stable after allocation.
- Remaining raw pool access is either removed or locally justified by the raw pool access rule.
- `KOF98.Game` remains independent of FFVM.
- Existing CS-simulation behavior remains functionally unchanged except for clearer failure modes.
- Focused tests cover the required strict, optional, debug, handle-stability, clear/reset, and snapshot-boundary cases.

## Application rule

When a later task says “apply this file”, treat this document as the source of truth over ad-hoc interpretation.
If current code conflicts with this contract, change the smallest implementation surface necessary while preserving the specified public shape and migration order.
If the contract itself is ambiguous, stop and ask for clarification instead of inventing a broader architecture.
If implementation pressure suggests a different architecture, update this contract first; do not let code become the hidden contract.
