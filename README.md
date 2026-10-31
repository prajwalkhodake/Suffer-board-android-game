# SKYRIFT

Original 3-lane 3D endless runner for Android (Unity 2022.3 LTS, URP, IL2CPP/ARM64).
A courier on a maglev hoverboard runs the vertical city; a Warden and a hound-drone are always one stumble behind.

> All names, characters, art, audio and level design are original. No third-party IP is referenced.

## Start here (humans and AI assistants)

| Read this | To learn |
|---|---|
| [`TECH_DOC.md`](TECH_DOC.md) | architecture contract, interfaces, data schemas, playbooks, budgets |
| [`CHANGELOG.md`](CHANGELOG.md) | what exists today, per version |
| [`ROADMAP.md`](ROADMAP.md) | what is planned / in flight |
| [`Docs/Decisions/`](Docs/Decisions/) | why the architecture is what it is (ADRs) |

## Repository layout

The repository root **is** the Unity project root:

```
Assets/_Project/   hand-authored game content and code (one folder per assembly)
Assets/ThirdParty/ vendored SDKs, unmodified
Docs/              ADRs, art/audio spec sheets, QA checklists
Packages/          pinned manifest + lockfile
ProjectSettings/   URP, IL2CPP/ARM64, API 26, portrait, Input System
```

## Setup

1. Install Unity **2022.3 LTS** with Android Build Support (SDK/NDK/OpenJDK).
2. Open this folder in Unity Hub (it is the project root).
3. First open: let package restore finish, then `Tools > Skyrift > Validate Catalogs`.
4. Build target: **Android**. Player settings are committed; do not change them locally.

Third-party SDKs (Firebase, Play Games, AdMob/UMP, Unity IAP) are added from the milestone they are needed in —
until then the project must compile and run against the mock service adapters.

## Milestones

M1 core runner → M2 collectibles/power-ups/scoring/crash-continue → M3 UI/meta/save →
M4 missions/events → M5 services → M6 polish → M7 release. Details in `ROADMAP.md`.
