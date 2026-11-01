# ADR-0014 — String pool keys, discrete origin roots, distance as a scalar

- **Status:** Accepted
- **Date:** 2026-11-01
- **Contract:** `TECH_DOC.md` §4.4 (`ForwardOffset`), §4.5, §4.6, §15 (revision 1.0.2)
- **Refines:** ADR-0007 items 1 and 3 (ADR-0007 remains Accepted — pooling-only and chunk recycling are unchanged)

## Context

ADR-0007 set the two hard rules — nothing instantiates mid-run, and the origin floats — before any of it was
written. Implementing them surfaced three questions the original wording answered in a shape that did not
survive contact with the code:

1. The pool contract was sketched as `GetPool<TComponent>(PoolKey)`: a component type *and* a key type.
2. The shift was described as translating "all active roots (track, player, chaser, particles, decals)" and
   as maths computed from `OriginOffset + local`.
3. Nothing said what a pooled object, or a running score, should do when the origin moves.

Point 2 contains a real contradiction. A chunk's Z is an accumulating spawn cursor *inside* the track root,
so translating the parent **and** letting the cursor accumulate counts the same distance twice — which is
exactly the precision drift the shift exists to remove. Point 1 collided with the catalog being a data asset
of strings.

## Decision

1. **Pools are keyed by `string`.** `IPoolRegistry.Register(PoolEntry, Transform)` / `GetPool(string key)` /
   `TryGetPool`. `PoolEntry` is (key, prefab, prewarm count). There is no `PoolKey` value type and no
   component-type addressing: what differs between pools is the **prefab and the budget**, and the behaviour
   that differs is carried by the `PooledEntity` subclass. `GetPool` on an unregistered key throws, naming
   both the key and the `PoolCatalog` asset — an authoring bug must be loud at boot, not `null` at spawn.
2. **Origin roots are discrete; anything that accumulates a coordinate is a listener.** Registered roots are
   the player rig, the chaser rig and the VFX root. The track root is **not** a root: `TrackGenerator`
   implements `IOriginShiftListener` and subtracts the delta from each live chunk's `localPosition.z`, so the
   chunk cursor and the world move together and nothing is double-counted. Released pooled objects are
   deliberately unregistered — they hold no live position.
3. **Run distance is an accumulated scalar.** `IPlayerMotor.DistanceRun` is integrated from speed × dt and is
   never read back off a transform, so a shift cannot change a score, a distance or the difficulty ramp. The
   rig's Z inside the current window is exposed as `ForwardOffset` (`DistanceRun` minus the accumulated
   shift) and **wraps** at the threshold; the chaser targets `ForwardOffset` rather than storing an absolute
   world Z. `OriginOffset` exists only for absolute-position bookkeeping (trails, decals, particle simulation
   space, audio occlusion).
4. **Overflow is loud but survivable.** A mid-run pool miss recycles the **eldest active** item (the one
   furthest behind the player, so the one the run has least use for), runs its release hook first, and
   reports `pool_overflow`. A pool that is empty while growth is frozen still creates one instance rather
   than handing `null` into the gameplay loop: the run continues while the defect stays visible in telemetry.
5. **Cadence.** `ShiftIfNeeded` performs at most **one** threshold per call. `TrackStreamer` calls it every
   frame, after coverage and recycle — so a sudden teleport unwinds over several frames instead of one visible
   pop, and a chunk spawned this frame is positioned with the offset it will actually keep.

## Consequences

### Positive

- The catalog, the registry and every spawner speak one identifier: a new pooled kind is one prefab plus one
  catalog row, with no C# type to register.
- A long run's numbers stay small **and** monotonic. Distance-based difficulty and score are independent of
  shift timing, so a 30 FPS device and a 120 FPS device agree on the same run's result.
- Origin correctness became testable without any gameplay: `FloatingOriginTests` asserts exact arithmetic
  (10 000 m of travel produces exactly four 2 048 m shifts and no float residue) against hand-built
  GameObjects.

### Trade-off

- Two registration mechanisms instead of one parent move, so a new absolute-position holder must know which
  side it is on. The rule: if it stores a coordinate that accumulates over the run, it is a listener; if it is
  a thing the player or the camera physically rides, it is a root.
- String keys lose compile-time checking. Mitigated by the editor `CatalogValidator` pass and by `GetPool`
  throwing with the key and the asset name.
- Create-once-on-overflow means a mis-sized catalog presents as a hitch rather than a crash; `OverflowCount`
  and `pool_overflow` telemetry are how it is found.
- One shift per call means an artificially large teleport takes several frames to settle. Normal play cannot
  produce that gap; a debug teleport can.

## Alternatives rejected

- **`GetPool<TComponent>(PoolKey)`:** two spellings for one concept, guaranteed to drift, and it excludes
  pooled content that is not a `Component` (chunk roots) from the shape it was designed for.
- **One shifted world parent:** double counting for anything that accumulates locally, and it moves parked,
  inactive pooled objects whose transforms are authoring state.
- **Deriving distance from the rig's world Z:** ties the score to a coordinate the game deliberately resets.
  This is the specific bug the old `OriginOffset + local` wording invited.
- **Throwing or returning `null` on pool exhaustion:** converts a tuning problem into a player-visible crash.

## Review trigger

- If gameplay maths ever reads `OriginOffset`, this decision has been broken — reopen it.
- If the threshold must move away from a power of two, or a shift becomes visible during normal play, revisit
  amortising the shift across frames via `IOriginShiftListener` staging (carried over from ADR-0007).
- If the catalog grows past roughly 50 keys, namespace keys by category (`chunk.*`, `obstacle.*`, `vfx.*`) and
  have `CatalogValidator` enforce the prefixes. Do not reintroduce a key type.
