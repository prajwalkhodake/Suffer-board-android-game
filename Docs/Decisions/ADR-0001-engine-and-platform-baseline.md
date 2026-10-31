# ADR-0001 — Unity 2022.3 LTS + URP, IL2CPP/ARM64, portrait Android

- **Status:** Accepted
- **Date:** 2026-10-31
- **Supersedes:** —
- **Contract:** `TECH_DOC.md` §2.1, §2.3
- **Upgrade path:** `TECH_DOC.md` §2.2

## Context

The game is a 3D endless runner targeting Android 8.0+ on mid- and low-end devices. We need
predictable plugin compatibility (Firebase, Play Games, AdMob/UMP, Unity IAP), a stable renderer
with mobile-first shaders, and Play Store compliance (64-bit, modern target SDK, small base install).

## Decision

1. Unity **2022.3 LTS**, URP (mobile shaders only, SRP Batcher on).
2. IL2CPP with **ARM64 only**; ARMv7 dropped (Play requires 64-bit, and dropping 32-bit cuts install size).
3. Portrait orientation only, `renderOutsideSafeArea` with explicit safe-area fitting.
4. Min API 26; target SDK follows Play's current requirement.
5. Package versions pinned in `manifest.json` + `packages-lock.json` committed.

Engine majors and plugin majors require an ADR + a regression branch, never an in-place bump.

## Consequences

**Positive:** best-known-good plugin matrix today; long LTS runway; familiar to any Unity contractor;
URP mobile shaders keep fill-rate cost predictable on low-end GPUs.

**Trade-off:** we sit one engine generation behind Unity 6 features (no URP 17 render graph).
Play Asset Delivery and Addressables APIs are stable on 2022.3, which matters more than new features.

## Alternatives rejected

- **Unity 6 LTS:** longer runway, but the Play Games plugin and several Firebase versions still need
  manual pinning; moving engines mid-project costs a full regression pass for no gameplay gain.
- **Built-in render pipeline:** fewer batching guarantees, worse mobile defaults, no SRP Batcher.

## Review trigger

Revisit when (a) Unity 2022.3 leaves LTS support, or (b) a required SDK raises its Unity minimum.
