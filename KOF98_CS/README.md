# KOF98_CS — CS-Simulation Layer

KOF98_CS is the **CS-simulation** sibling of `KOF98/`. It runs the same
fighting-game framework, but with skill behaviors implemented as plain C#
objects rather than FFVM script instances.

## Layered architecture

```
┌──────────────────────────────────────────────────────┐
│  KOF98_CS  (executable: this folder)                 │
│   • Wires everything up; per-frame loop              │
│   • Re-uses RaylibGameView from KOF98.Game           │
└──────────────────────────────────────────────────────┘
            │ references                  │ references
            ▼                             ▼
┌──────────────────────────┐    ┌────────────────────────────────────┐
│  KOF98.CsSim (library)   │    │  KOF98.Game (library)              │
│   • IdleBehavior         │    │   • Game world + frame loop        │
│   • WalkForwardBehavior  │───▶│   • Skill running framework        │
│   • WalkBackwardBehavior │    │   • ISkillBehavior + SkillContext  │
│   • CsSimSkillCatalog    │    │   • Raylib view + Console view     │
└──────────────────────────┘    │   • Combat / Effect / Projectile   │
                                └────────────────────────────────────┘
```

The split mirrors the design discussion:

- **Game layer (`KOF98.Game`)** — pure C# fighting-game framework; *no* FFVM
  dependency. Defines `ISkillBehavior` (Spawn / Tick / Kill — close to
  `VMWorld` per-instance lifecycle) and the skill running framework
  (`SkillManager`, candidate pool, collision-frame timeline, etc.).
- **CS-simulation layer (`KOF98.CsSim`)** — `ISkillBehavior` impls in plain
  C#, used as a parallel implementation and a baseline for comparing against
  the future VM-backed version.
- **VM layer** *(future, out of scope here)* — a separate library that
  implements `ISkillBehavior` by wrapping `VMWorld.SpawnInstance` /
  `TickInstance` / `KillInstance`. KOF98 (the existing app) will adopt it.

## Skills supported in this initial cut

| Skill           | FFS analogue                          | Tags          |
|-----------------|---------------------------------------|---------------|
| `Idle`          | `skill_idle.ffs`                      | `TAG_IDLE`    |
| `WalkForward`   | `skill_walk_forward.ffs` (forward)    | `TAG_WALK`    |
| `WalkBackward`  | `skill_walk_forward.ffs` (backward)   | `TAG_WALK`    |

Attacks, jumps, hits, blocks, projectiles etc. are intentionally not wired
up yet — the framework code from KOF98 is preserved so they can be added
incrementally later.

## Running

```bat
:: Windows one-click
KOF98_CS\kof98_cs-init.cmd
KOF98_CS\run-kof98_cs.cmd
```

```bash
# Cross-platform
dotnet build KOF98_CS/KOF98_CS.csproj -c Release
dotnet run --project KOF98_CS/KOF98_CS.csproj -- --raylib
dotnet run --project KOF98_CS/KOF98_CS.csproj -- --headless --frames 600
```

The Raylib view and UI control panel are exactly the ones from `KOF98/`,
re-used from `KOF98.Game/View/`.

## Relationship to `KOF98/`

This folder is **independent**: building or running KOF98_CS does not
require `src/FFVM/FFVM.csproj` and does not touch `KOF98/`. The existing
KOF98 app continues to work unchanged.

When KOF98 is migrated to use the same shared `KOF98.Game` library, it
will plug in a `VMSkillBehavior` from a new `KOF98.Vm` library while
keeping everything else intact.
