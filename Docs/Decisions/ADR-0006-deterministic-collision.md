# ADR-0006 — Deterministic, physics-free collision resolution with a two-hit stumble

- **Status:** Accepted
- **Date:** 2026-10-31
- **Contract:** `TECH_DOC.md` §4.7

## Context

The genre's feel depends on collision outcomes being *fair and legible*: jumping over a low barrier
must always work, rolling under a high one must always work, a side scrape must warn rather than kill,
and a head-on hit must be fatal. Unity rigidbody physics gives us tunnelling at high speed, frame-rate
dependent outcomes, and emergent cases we cannot reason about — the classic cause of "this game cheated
me" reviews.

## Decision

1. Contact detection uses trigger volumes + cached transforms (no `Rigidbody` interaction for obstacles).
2. `ICollisionResolver.Resolve(in ContactContext)` is a **pure function**: same input ⇒ same outcome. It
   returns `Cleared | Grazed | Stumbled | Crashed | Ignored`.
3. The only state it owns is the run's stumble counter (exposed, resettable): first side scrape =
   `Stumbled`, second = `Crashed`; head-on = `Crashed` immediately; absorb charges convert a crash into
   `Grazed` and consume a charge.
4. Per-archetype rules live in `IObstacleBehaviour` implementations selected by `behaviourId` from a data
   catalog, so new obstacles are data (`jumpable`, `rollable`, `full_blocker`, `moving`, `ramp`,
   `grazeable`) or one new behaviour class.
5. Clearance checks compare against the player's actual vertical state and a data-defined clearance
   profile, with explicit forgiveness margins (a jump "counts" slightly before/after the visual apex).

## Consequences

**Positive:** deterministic and unit-testable (a 60-case matrix runs in milliseconds), immune to speed
and framerate, no rigidbody jitter, cheapest possible per-frame cost, and designers get forgiveness
margins as data. Every crash cause is an enum value we can analyse.

**Trade-off:** no emergent physics interactions (we do not want them in a runner), and trigger volume
authoring must match visuals — enforced by the obstacle clearance debug overlay and validation rule.

## Alternatives rejected

- **`OnCollisionEnter` with rigidbodies:** tunnelling at high speed, framerate coupling, unpredictable.
- **Continuous collision detection only:** fixes tunnelling but not the fairness of the outcome.
- **Capsule casts vs all geometry:** still needs the same outcome policy, more cost, no benefit.

## Review trigger

If we add physics-driven set pieces (destructible props, moving platforms the player rides), extend the
resolver with a `IContactOutcomePolicy` implementation rather than reintroducing rigidbody contact rules.
