# ADR-0007 — Pooling only, plus track recycling with a floating origin

- **Status:** Accepted
- **Date:** 2026-10-31
- **Contract:** `TECH_DOC.md` §4.6, §11.2
- **Refined by:** ADR-0014 — pool keys are strings (not component + key type), origin roots are discrete with
  listeners re-basing accumulators, and run distance is an accumulated scalar. Pooling-only and chunk
  recycling are unchanged.

## Context

Two hard constraints collide in an endless runner: the run must never allocate (Android GC pauses
manifest as stutter, which is fatal at 60 FPS on a 4 GB device), and world coordinates grow without
bound, which eventually destroys float precision (jitter in transforms, physics, particles, audio).

## Decision

1. **Nothing is instantiated or destroyed during a run.** Every runtime object (chunks, obstacles,
   collectibles, VFX, audio sources, UI popups) comes from a pool registered in a data `PoolCatalog`
   with a prewarm count. Pools are grown only during the loading phase; a mid-run growth attempt logs
   `pool_overflow` and reuses the eldest active item instead of allocating.
2. **Track recycling:** chunks behind the player are released back to their pool and re-used ahead.
3. **Floating origin:** when the player passes a fixed distance threshold, all active roots (track,
   player, chaser, particles, decals, audio) are translated by `-threshold` and a double
   `OriginOffset` is incremented. All distance/score maths uses `OriginOffset + local`.
   *(as written — corrected by ADR-0014: the track root is not translated, and distance/score maths must never
   read `OriginOffset`; it is bookkeeping for absolute-position consumers only)*

## Consequences

**Positive:** measured 0 B/frame in steady state; no GC spikes; precision remains constant at any run
length (a 1-hour run behaves like a 30-second one); pooling also removes spawn-frame hitches.

**Trade-off:** pooled objects must be perfectly reset (`IPoolable.OnDespawned`), which is a class of bug
that only shows up on reuse. Mitigations: a `Leak Check` tool that diffs active objects across runs, a
PlayMode soak test, and a hard rule that every `OnSpawned` sets **all** fields it reads.

## Alternatives rejected

- **Instantiate/Destroy with `ObjectPool<T>` from Unity:** still allocates on growth, and addressing
  precision does nothing for the physics/particle jitter.
- **Moving-world (player stays at 0):** an alternative floating origin that would require re-writing all
  chunk authoring and breaks physics for the chaser and props; our shift is cheaper and localised.
- **`double`-precision transforms:** Unity transforms are `float`; not an option.

## Review trigger

If the shift threshold causes visible hitching, shift during a loading frame or amortise across frames
using `IOriginShiftListener` staging — do not raise the threshold above float-safe range.
