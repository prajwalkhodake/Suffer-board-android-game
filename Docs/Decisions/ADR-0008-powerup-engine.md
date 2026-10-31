# ADR-0008 — Power-up engine: rules in data, mechanics in `PowerUpEffectBehaviour`

- **Status:** Accepted
- **Date:** 2026-10-31
- **Contract:** `TECH_DOC.md` §4.10, §5.8

## Context

Power-ups are the most frequently added feature in a runner's live-ops cycle, and each one usually
arrives with a new timer, stacking question and interaction bug. If every power-up gets its own
`Update()`, its own stacking logic and its own HUD path, the system rots: ten power-ups = ten bugs about
"what happens when I grab X while Y is active".

## Decision

Split every power-up into three layers:

1. **Data** (`PowerUpDefinition`): stacking rule enum, priority, duration, charges, level table,
   activation condition, channel contributions, assets.
2. **Engine** (`IPowerUpService`): one timer, one activation/deactivation pipeline, one `Tick(dt)`
   driven by the run controller, one `Snapshot` for the HUD, one reset path per run.
3. **Mechanics** (only when channels are not enough): a `PowerUpEffectBehaviour` ScriptableObject with
   `OnActivated/OnTick/OnDeactivated`.

Effects contribute to named **channels** (`Speed`, `JumpHeight`, `PickupRadius`, `ScoreMultiplier`,
`CrashAbsorb`, `Invulnerability`, `FxIntensity`). Consumers (motor, scoring, collectibles, collision)
read channel aggregates. Priority and stacking are resolved once, in data order, by the engine.

## Consequences

**Positive:** 90% of new power-ups are pure data; stacking conflicts are resolved in one place and
tested as an N×N matrix; the HUD needs only a snapshot; expiry is deterministic under `IClock` so tests
fast-forward instead of waiting real seconds; adding one that needs real mechanics means one new class,
zero engine edits.

**Trade-off:** channel-based effects are less expressive than arbitrary code, and a mis-authored channel
magnitude can silently be very strong. Mitigation: catalog validation clamps magnitudes and the debug
HUD shows active channel aggregates.

## Alternatives rejected

- **Per-power-up `MonoBehaviour` with its own `Update`:** N timers, N reset paths, guaranteed stacking bugs.
- **One giant `enum PowerUpType` switch:** violates Open/Closed; every new power-up edits the switch.
- **Timeline/Animator-driven effects:** not testable, not data-driven, not tunable by live-ops.

## Review trigger

If channel aggregation itself starts needing ordering semantics beyond `priority` (e.g. multiplicative
chaining rules per channel), add a channel-composition strategy to data rather than special-casing code.
