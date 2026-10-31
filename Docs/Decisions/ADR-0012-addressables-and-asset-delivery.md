# ADR-0012 — Addressables for all content, Play Asset Delivery for the base-size budget

- **Status:** Accepted
- **Date:** 2026-10-31
- **Contract:** `TECH_DOC.md` §2.3, §4.20, §5 (addresses), §11.1

## Context

Play requires the base download to stay small (a runner loses installs above roughly 150 MB), while the
game must eventually ship multiple worlds, characters, music stems and five locales. Shipping everything
in the base module also means every store update re-downloads everything and every event needs a new
build.

## Decision

1. **Addressables is the only content loading mechanism.** Every screen prefab, character, world,
   chunk set, audio bank and locale pack is an Addressables group with a stable address convention
   (`<type>/<id>`, `world.<id>`, `loc.<locale>`).
2. Content is addressed **by `StableId`**, so code never holds direct asset references across module
   boundaries and content can be validated before it is loaded (`IGameDatabase`).
3. **Play Asset Delivery** backs the base-size budget: install-time pack for core worlds and UI, fast-follow
   packs for seasonal events and extra locales.
4. Groups are built per world/event and versioned by a `contentVersion` reported through Remote Config, so
   an event can be enabled/disabled without a client update.
5. Reference counting is explicit: load on screen/world entry, release on exit; a leak check runs across
   ten runs in QA.
6. `Skyrift.Data` is loadable in EditMode, so catalogs can be validated by the build script before any
   bundle exists.

## Consequences

**Positive:** base install stays inside budget; events and locales ship without a store update; every
asset has a stable, reviewable address; content can be validated without entering play mode; worlds can be
unloaded to reclaim memory (a hard requirement for the 1.2 GB peak budget).

**Trade-off:** asynchronous loading adds a state to every screen and system that needs an asset, and
mishandled reference counting causes either leaks or blank screens. Mitigation: `IAssetHandle<T>` wrapper with
a scope object (`using`-style), a boot-time "all addresses resolve" check, and the leak checker in CI.

## Alternatives rejected

- **Resources folder:** no unloading, bloats base install, deprecated direction.
- **Direct references in prefabs/SOs:** everything in the base module, huge updates, slow domain reload.
- **AssetBundles by hand:** reinvents Addressables' dependency tracking and ref-counting badly.
- **Everything as a remote download:** violates first-run offline playability and the store's install UX.

## Review trigger

If Addressables' memory overhead on low-end devices becomes measurable, downgrade to a thinner custom
catalog over `AssetBundle` — keep the `StableId` addressing contract intact so nothing else changes.
