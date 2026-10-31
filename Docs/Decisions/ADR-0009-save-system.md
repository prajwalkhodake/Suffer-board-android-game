# ADR-0009 — Sliced, versioned, encrypted local save with a migration chain

- **Status:** Accepted
- **Date:** 2026-10-31
- **Contract:** `TECH_DOC.md` §4.15, §5.15, §8

## Context

A runner's save file is the player's whole account: currency, unlocks, mission progress, streak. Two
failure modes kill retention: an update that breaks old saves, and a corrupted/edited save that wipes
progress or hands out free currency. We also need to delete data on request (privacy compliance).

## Decision

1. **One JSON document, several independent slices** (`economy`, `ownership`, `progression`, `settings`,
   `stats`, `missions`, `events`, `run`). A broken slice is dropped and re-defaulted without losing the rest.
2. **Explicit `schemaVersion` + one `IMigrationStep` per version hop** (`N → N+1`), pure functions over
   JSON nodes, individually unit-tested with a permanent golden fixture per shipped version.
3. **Forward compatibility:** a save newer than the client (downgrade) is loaded read-only; never migrated
   backwards, never overwritten.
4. **Encryption:** AES-256-GCM over the canonical JSON, key derived from a device-stable id plus an app
   secret, with an `encVersion` field so the key can be rotated via migration. Failure to decrypt ⇒
   quarantine + defaults + `save_recovered`, never a crash.
5. **Atomic writes:** temp file → fsync → rename, keeping one `.bak`. Checksum detects truncation.
6. **Additive fields** with safe defaults need no version bump; renames/semantic changes always do.
7. Reset (data deletion) wipes local and queues a cloud delete.

## Consequences

**Positive:** updates never brick a save (migration chain is a compile-blocking contract, and set
invalidation is impossible because fixtures are kept forever); a corrupt slice costs one system, not the
account; casual save editing is defeated; the ledger+revision fields make support cases diagnosable.

**Trade-off:** migration code accumulates and must be maintained forever (accepted cost, mitigated by
resolution-through-additive-fields); encryption means a save cannot be recovered without the device key,
hence the cloud copy as the recovery path.

## Alternatives rejected

- **`JsonUtility` + `PlayerPrefs`:** no schema control, no slicing, plaintext, size limits.
- **Binary serialization (`BinaryFormatter`-style):** unreadable diffs, brittle across code changes,
  security hazards.
- **Server-authoritative only:** kills offline play, which is mandatory for a mobile runner.
- **No encryption, only checksum:** trivially editable, undermines the economy.

## Review trigger

Revisit if we move the economy server-authoritative (then local currency becomes a cache and the slices
split further), or if key derivation needs hardware-backed storage.
