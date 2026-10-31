# CHANGELOG — SKYRIFT

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) · versioning: SemVer on `bundleVersion`.
**This file changes every release. `TECH_DOC.md` does not.**

---

## [Unreleased]

### Current state

Nothing shipped yet. The repository contains only the architecture contract (`TECH_DOC.md`), the ADR set (`Docs/Decisions/`) and the roadmap. No Unity project, no code.

Next milestone: **M1 — core runner**.

---

## [0.1.0] — M1 core runner (planned)

### Planned

- Unity 2022.3 LTS project scaffold: URP, IL2CPP/ARM64, API 26, portrait, new Input System, Addressables, TextMeshPro.
- Assembly definitions for Core, Data, Services.Abstractions, Services, Audio, Controls, Track, Economy, Meta, Gameplay, UI, Bootstrap, Editor, Tests.
- Core primitives: event bus, service container, state machine, object pool registry, clock, seeded random, `StableId`, logging.
- Controls: swipe/tap detection with buffered intents.
- Gameplay: player motor, lane interpolation, jump/roll, speed profile.
- Track: chunk repository, weighted difficulty-aware generator, chunk pooling, floating origin.
- Obstacles: jumpable / rollable / full-blocker behaviours, collision resolver with two-hit stumble.
- Chaser proxy driven by proximity.
- EditMode tests: generator determinism + reachability, collision matrix.

---

## [0.2.0] — M2 collectibles, power-ups, scoring, crash/continue (planned)

---

## [0.3.0] — M3 UI, meta screens, save system (planned)

---

## [0.4.0] — M4 missions, events, daily rewards, achievements (planned)

---

## [0.5.0] — M5 services (ads, IAP, analytics, cloud, Play Games) (planned)

---

## [0.6.0] — M6 polish, audio/VFX, localization, optimization, tests (planned)

---

## [1.0.0] — M7 release (planned)
