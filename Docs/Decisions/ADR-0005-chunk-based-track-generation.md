# ADR-0005 — Hand-authored chunks + seeded, weighted, data-driven generator

- **Status:** Accepted
- **Date:** 2026-10-31
- **Contract:** `TECH_DOC.md` §4.5, §5.4

## Context

An endless runner lives or dies on track variety vs fairness. Fully procedural geometry is unfun and
impossible to art-direct; a fixed sequence is not endless. We also need reproducibility (daily
challenges, bug reports) and the ability to add content without touching the generator.

## Decision

1. The unit of generation is a **hand-authored chunk prefab** plus a `ChunkDefinition` (tags, length,
   difficulty band, entry/exit lane masks, weight, cooldown, slots).
2. Selection is a pure function of: player distance, current difficulty, world tags, previous chunk's
   exit mask, per-chunk cooldowns, and pluggable `IChunkEligibilityRule` / `IChunkSelectionWeight`
   contributors. Final pick is a weighted random draw from the seeded RNG.
3. **Guaranteed fallback:** if no candidate is eligible, a world's `safeFallback` chunk is used and
   `track_generation_fallback` is reported. Generation never stalls.
4. **Determinism contract:** same seed + same catalogs + same Remote Config ⇒ same chunk sequence.
5. Reachability is a test, not a hope: `GeneratorReachabilityTests` simulates thousands of chunks and
   asserts at least one traversable lane always exists.
6. Chunks are pooled; the generator deals in handles, never in `Instantiate`.

## Consequences

**Positive:** artists own feel and difficulty; designers tune via weights and bands; a new chunk is a
prefab + asset (playbook §7.4) with zero code; determinism gives reproducible runs and testable logic;
generation cost per chunk is a handful of comparisons, no search.

**Trade-off:** variety is bounded by authored content, and someone must playtest chunks. Weight bugs
produce repetitive runs, which the repeat penalty and the 10k-chunk simulator mitigate.

## Alternatives rejected

- **Tile/cell-based procedural generation:** infinite variety, almost always unfair or boring, and
  impossible to art-direct for a premium-feeling runner.
- **Grammar/constraint solver:** best output quality, but unpredictable cost per chunk and a large
  authoring surface for a fraction of the benefit.
- **Fixed authored track:** not endless, becomes memorisation.

## Review trigger

If authored chunk count per world exceeds the point where manual playtesting cannot keep up, evaluate a
chunk-grammar layer that composes chunks from validated sub-patterns.
