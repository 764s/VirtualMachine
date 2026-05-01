# Intent-Bound Data Manager Contract

Status: implementation contract, not a user-facing design note.
Location rule: this file lives next to the implementation it constrains. Do not move it to `Docs/` or another general documentation area.

## Purpose

This file is the intermediate specification for a future intent-bound data manager in `KOF98.Game`.
When an implementation request says “apply this file”, the resulting code should converge on the same structure, semantics, and constraints even across separate sessions.

The target implementation is a replacement or evolution of the current static-definition access pattern around `GameCatalog`, `Pool<T>`, and ECS components that store integer definition handles.

## Problem intent

The game layer has two categories of data access:

1. Reasonable gameplay access: systems and skill behavior ask for data that should exist in the current simulation contract.
2. Accidental or out-of-contract access: debug views, partially initialized entities, stale handles, invalid ids, exploratory tooling, or future migration code ask for data outside that contract.

The future manager must make that distinction explicit. It must not make every lookup silently nullable, and it must not make every lookup fatal. The caller's stated intent determines the access semantics.

## Non-goals

- Do not implement a general database, asset pipeline, serializer, or dependency injection container.
- Do not replace ECS component arrays with object graphs.
- Do not make static definitions mutable runtime state.
- Do not add FFVM dependencies to `KOF98.Game`.
- Do not change the existing `KOF98/` app as part of applying this contract unless a later task explicitly asks for migration.
- Do not move this contract into `Docs/`; it must remain with the constrained implementation.

## Terms

- Definition data: authoring/static data such as characters, stats, movement definitions, skill loadouts, and skill definitions.
- Runtime data: mutable ECS world state stored in `GameWorld` component arrays.
- Handle: an integer id stored in runtime data that points to definition data.
- Intent: the lookup mode selected by the caller before reading data.
- Strict access: a lookup that must succeed or throw a deterministic exception.
- Optional access: a lookup that may fail and returns a stable fallback value.
- Debug access: a lookup for display, diagnostics, or tooling that must not change simulation behavior.

## Required public shape

The manager must expose intent-specific lookup APIs. Exact names may vary, but the semantic split is mandatory:

1. Strict API
   - Used by gameplay systems when the data is required by simulation invariants.
   - Invalid ids, missing definitions, and wrong-id-kind accesses throw deterministic exceptions.
   - Exceptions must include the id, requested definition kind, and caller-facing context when provided.

2. Optional API
   - Used where absence is a valid state, for example `InvalidId` or “no active skill”.
   - Missing data returns `null`, `false`, or an explicit result object according to the type style chosen by the implementation.
   - Optional access must not throw for ordinary absence.

3. Debug API
   - Used by views, console output, diagnostics, and migration probes.
   - Must be best-effort and stable enough for display.
   - Must not mutate manager state.
   - Must not hide strict gameplay bugs by being used inside core simulation paths.

## Required behavior

- Valid strict lookup returns the exact stored definition object or value.
- Invalid strict lookup fails immediately and deterministically.
- Optional lookup with an invalid id returns absence without logging by default.
- Debug lookup with an invalid id returns a readable placeholder or absence marker.
- Allocation returns stable integer handles for the lifetime of the manager contents.
- Existing handles must not be recycled within the same manager contents.
- Clear/reset is allowed for tests and bootstrapping, but must be explicit.
- Definition data is not part of `GameSnapshot`; snapshots store runtime handles only.
- Snapshot restore assumes the same compatible definition manager contents are available.

## Data ownership and lifecycle

- Definition data belongs to the manager/catalog layer.
- Runtime ECS components only store handles and mutable state.
- Skill behavior can read definition data through context objects, but must not own global definition registries.
- Views can read through debug or optional access, not through strict gameplay-only helpers.
- Tests may reset manager contents, but production frame code must not clear global definitions mid-frame.

## ECS constraints

- Keep `GameWorld` as dense component arrays indexed by entity slot.
- Keep `EntityId` generation checks separate from definition-handle validation.
- Do not introduce per-entity heap objects as the primary simulation state.
- Do not store live behavior instances in snapshots.
- Preserve the current separation between snapshot-friendly value components and transient caches.

## Integration constraints

- The first implementation should be a small evolution near `GameCatalog`, not a broad rewrite.
- Existing call sites may be migrated gradually from direct pool indexing to intent-specific access.
- Hot-path systems may keep direct array access only when the invariant is already enforced nearby and documented by the call pattern.
- New code should prefer intent-specific access over raw `Pool<T>` indexing.
- `GameCatalog.InvalidId` remains the canonical no-data sentinel unless a later migration replaces all callers in one step.

## Error constraints

Strict-access errors must be deterministic and actionable. They must not depend on random data, frame timing, or logging configuration.

Minimum error fields:

- definition kind
- requested id
- valid count or range
- operation/context string, when supplied by caller

Optional and debug access must not emit noisy logs for expected absence. If diagnostics are needed, expose them through explicit debug methods.

## Determinism constraints

- Applying this contract must not introduce nondeterministic definition ordering.
- Allocation order defines ids.
- Iteration over definitions must be stable and insertion ordered unless a later contract explicitly changes it.
- No random fallback data is allowed in gameplay paths.
- “Random” or undefined behavior is permitted only as a documented non-contract outside manager APIs; manager APIs themselves must be deterministic.

## Implementation boundaries

Allowed first-step changes when applying this contract:

- Add a manager/catalog type or evolve `GameCatalog` with intent-specific methods.
- Add small result/exception helper types if they reduce ambiguity.
- Update direct lookup call sites that currently rely on required data.
- Update debug/view call sites to use optional/debug access.
- Add focused tests for strict, optional, and debug lookup semantics.

Disallowed first-step changes:

- Rewriting unrelated combat, physics, input, or rendering systems.
- Migrating `KOF98/` to the CS-simulation path.
- Adding new package dependencies.
- Replacing ECS arrays with dictionaries or object ownership graphs.
- Making snapshots own or serialize definition data.

## Acceptance criteria

An implementation satisfies this contract when:

- Required gameplay lookups use strict semantics or have an adjacent invariant that makes raw access safe.
- Optional states such as no active skill are not represented as unexpected exceptions.
- Debug display of missing data is stable and readable.
- Invalid required ids fail with deterministic, context-rich errors.
- Definition handles remain stable after allocation.
- `KOF98.Game` remains independent of FFVM.
- Existing CS-simulation behavior remains functionally unchanged except for clearer failure modes.

## Application rule

When a later task says “apply this file”, treat this document as the source of truth over ad-hoc interpretation. If current code conflicts with this contract, change the smallest implementation surface necessary to satisfy the contract. If the contract itself is ambiguous, stop and ask for clarification instead of inventing a broader architecture.
