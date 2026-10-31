# ADR-0011 — Typed Remote Config keys with local defaults, plus feature flags

- **Status:** Accepted
- **Date:** 2026-10-31
- **Contract:** `TECH_DOC.md` §4.16, §9

## Context

Live balance tuning (speed, difficulty, ad cadence, event schedule) must ship without a build. But
Remote Config is a remote, eventually-inconsistent key-value store: keys can be missing, wrong-typed, or
set to nonsense by an operator at 2 a.m. It is also a network dependency that must never block or crash
a single-player game.

## Decision

1. Every key is declared **once**, typed, in a static `RemoteConfigKeys` class, alongside a default value
   in the `RemoteConfigDefaults` asset (shipped with the client) and a **clamp range**.
2. Reads happen only through `IRemoteConfigService.Get<T>(key)`; the service returns the default when the
   key is absent, unparsable or fetch failed, and **clamps** out-of-range values while reporting
   `rc_out_of_range`.
3. `RemoteConfigSource` (`Default | Cached | Network`) is exposed and reported in analytics, so a balance
   investigation can tell whether a player saw network values at all.
4. Major features are additionally exposed as **feature flags** with a safe default. With every flag off
   the game must boot, be playable and be saveable. Flags are readable through `IFeatureFlags` so a gate
   can layer per-world/per-AB overrides without touching consumers.
5. Types are limited to `bool`, `int`, `float`, `string`, `json`. Anything more complex is a `json` key
   with a schema and a validator, not fifty keys.

## Consequences

**Positive:** no hardcoded tuning in code; operators can move balance without a release; a bad remote
value degrades instead of breaking (clamp + default); offline play unaffected; the registry in §9 is the
single review surface for live-ops changes.

**Trade-off:** the typed-key ceremony (declare key, default, clamp, doc row) is real work per knob, and
cached values can briefly disagree with the console. Mitigation: no key is added without its §9 row, and
the debug overlay shows current values + source.

## Alternatives rejected

- **Raw string keys scattered in code:** typos become silent default-fallbacks; impossible to audit.
- **Remote Config as the only source of truth:** an offline player would have no balance at all.
- **Full remote content (JSON catalogs) for all balance:** tempting, but it removes compile-time
  validation and makes reviews of gameplay changes impossible; reserved for specific JSON keys only.

## Review trigger

If live-ops needs to ship content (not just numbers) without a build, extend the `json` key path with a
schema validator rather than adding hundreds of primitives.
