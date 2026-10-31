# ADR-0003 — In-house service container instead of a DI framework

- **Status:** Accepted
- **Date:** 2026-10-31
- **Contract:** `TECH_DOC.md` §4.0, §4.1

## Context

We need dependency inversion for services (ads, IAP, analytics, cloud, save) so gameplay and UI can run
against mocks. Third-party DI containers bring reflection, IL2CPP stripping risk, allocations at
resolution and a learning curve for contractors. Unity's own patterns (singletons, `FindObjectOfType`)
are the thing we are trying to avoid.

## Decision

Implement a ~150-line `IServiceContainer` in `Skyrift.Core`:

- `Register<T>(instance)`, `RegisterLazy<T>(Func<T>)`, `Resolve<T>()`, `TryResolve<T>(out T)`.
- Resolution happens at composition time (`Bootstrap`) and at construct time (`Construct(...)` on
  spawned systems). No per-frame `Resolve`.
- Backed by a `Dictionary<Type, object>` built once; no reflection, no attribute scanning.
- Nested scopes for a run (`CreateRunScope()`) so per-run objects are released deterministically.

`Skyrift.Bootstrap` is the only assembly allowed to call `Register`. Everything else only depends on
interfaces it is handed.

## Consequences

**Positive:** trivially IL2CPP-safe, zero alloc after boot, full determinism, easy to fake in PlayMode
tests, no third-party dependency in the most critical code path.

**Trade-off:** we lose automatic constructor injection magic and lifetime validation. Mitigation: boot
order is explicit and documented (§4.1) and a boot-time `ValidateContainer()` fails fast on missing
registrations in `DEV`.

## Alternatives rejected

- **Zenject/Extenject:** heavy, reflection-based, large domain-reload cost, well-known stripping pitfalls.
- **VContainer:** better, but still an unneeded dependency for ~40 services and adds a learning curve.
- **Static singletons:** untestable, order-dependent, hides dependencies.

## Review trigger

If a feature genuinely needs AOP/interception/scoped lifetimes beyond run scopes, revisit with an ADR.
