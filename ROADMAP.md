# ROADMAP — SKYRIFT

**This file changes whenever plans change. `TECH_DOC.md` does not.**

Legend: `[ ]` not started · `[~]` in progress · `[x]` done · `[!]` blocked (reason in line)

---

## Status snapshot

| Milestone | State | Gate |
|---|---|---|
| M0 Architecture contract | `[x]` | `TECH_DOC.md` approved |
| M1 Core runner | `[ ]` | playable run, 0 B/frame GC, generator tests green |
| M2 Collectibles / power-ups / scoring / crash-continue | `[ ]` | full loop crash → continue → results |
| M3 UI / meta / save | `[ ]` | save survives kill, migration test green |
| M4 Missions / events / daily / achievements | `[ ]` | mission set completion raises multiplier |
| M5 Services (ads / IAP / analytics / cloud / Play Games) | `[ ]` | mockable in editor, sandbox-verified on device |
| M6 Polish (audio / VFX / loc / perf / tests) | `[ ]` | §11 budgets met on mid + low end |
| M7 Release | `[ ]` | §12.3 checklist complete |

---

## M1 — Core runner

- [ ] Unity 2022.3 project scaffold + pinned packages + Android player settings + URP assets + quality tiers
- [ ] Assembly definitions + folder skeleton + `link.xml`
- [ ] Core: event bus, service container, state machine, pool registry, `IClock`, seeded RNG, `StableId`, `ILog`
- [ ] Bootstrap: composition root, boot steps, `Boot.unity` + `Main.unity`
- [ ] Controls: `InputActionAsset`, swipe/tap detector, intent buffer, keyboard test source
- [ ] Gameplay: player motor (lanes, jump, roll, speed profile), animation driver
- [ ] Track: `ChunkDefinition` + repository, weighted generator, chunk pool, floating origin
- [ ] Gameplay: `ICollisionResolver` + jumpable/rollable/full-blocker behaviours, stumble counter
- [ ] Chaser: proximity model + placeholder rig
- [ ] HUD debug overlay (FPS, GC, lane, speed, distance, stumble count)
- [ ] EditMode: generator determinism, reachability, collision matrix, pool no-alloc
- [ ] PlayMode: 60 s soak with 0 B/frame allocations

**M1 exit:** one hand-authored world with ≥ 8 chunks runs endlessly on device at 60 FPS with no GC spikes.

---

## M2 — Collectibles, power-ups, scoring, crash/continue

- [ ] `CollectibleDefinition` + patterns (line/arc/over-obstacle/zigzag) + pooling + magnet channel
- [ ] Letter/word event framework
- [ ] `PowerUpService` (stacking rules from data) + jetpack, magnet, 2x, super sneakers, shield/hoverboard
- [ ] `ScoreCalculator` + `ScoreBreakdown`, combo model
- [ ] `RunController`: crash freeze, `IContinueOfferPolicy`, escalating key cost, ad/keys continue
- [ ] Results summary pipeline
- [ ] EditMode: stacking matrix, scoring math; PlayMode: power-up lifetime, continue flow

---

## M3 — UI, meta, save

- [ ] `IUINavigator`, screen prefabs, safe-area fitter, HUD
- [ ] Screens: Splash, Loading, Home, Character Select, Shop, Settings, Pause, Continue, Results, Daily, Events, Leaderboards
- [ ] Localization base (`en`) + key pipeline + pseudo-locale
- [ ] Save: slices, encryption, atomic write, `SaveMigrator`, fixtures
- [ ] Economy: wallet, ledger, shop service, price rules, simulated IAP adapter
- [ ] Ownership, characters, outfits, boards, trails
- [ ] Tutorial: data-driven first-run steps

---

## M4 — Missions, events, daily, achievements

- [ ] Signal adapter (domain events → mission progress)
- [ ] `IMissionEvaluator` set (distance, coins, powerups, jumps, no-crash, continues, world-specific)
- [ ] Mission set assembly, rotation, multiplier feedback into scoring
- [ ] Daily login + streak, daily challenges
- [ ] Mystery box reward tables + pity
- [ ] Events / world tour with RC schedule and downloadable content packs
- [ ] Achievements binding

---

## M5 — Services

- [ ] Firebase: Analytics, Crashlytics, Remote Config, Auth, Firestore cloud save
- [ ] AdMob + UMP + consent flow + frequency policy
- [ ] Unity IAP + Play Billing, restore, idempotent grants
- [ ] Google Play Games: sign-in, leaderboards, achievements
- [ ] Mock adapters for every service + offline-first boot path
- [ ] Remote Config registry materialized (§9), feature flags wired

---

## M6 — Polish

- [ ] Adaptive music stems + intensity driver, SFX bank, mixers
- [ ] VFX pass, camera feel (shake, FOV, follow), speed lines
- [ ] 5 locales, per-locale fonts, RTL check
- [ ] Accessibility: colorblind palettes, haptics toggle, reduced motion
- [ ] Perf: budgets §11 met, device matrix verified
- [ ] Test coverage pass for all public contracts

---

## M7 — Release

- [ ] AAB, Play Asset Delivery, symbols, Play Console listing, data safety form
- [ ] Staged rollout + kill switches verified

---

## Open questions (blocking decisions)

| # | Question | Blocks |
|---|---|---|
| 1 | Do we need a server-authoritative economy at launch, or client-side with IAP receipt validation only? | M5 |
| 2 | Cloud save provider: Firebase Firestore only, or also Play Games Saved Games as a fallback? | M5 |
| 3 | Do leaderboards use Play Games only, or also an in-game seasonal board? | M4 / M5 |

---

## Upgrade watchlist (see `TECH_DOC.md` §2.2)

- Firebase Unity SDK release cadence.
- Play Games plugin version vs Unity version matrix.
- Addressables API churn between 2022.3 and Unity 6.
