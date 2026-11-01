# ADR-0013 — Contract-unit files instead of strict one-type-per-file

- **Status:** Accepted
- **Date:** 2026-11-01
- **Contract:** `TECH_DOC.md` §13.2 rule 1 (revision 1.0.1)

## Context

The original convention ("one public type per file") was written before implementation. In practice the
architecture is built from small contract units: an interface plus one or two implementations plus the
enums and `readonly struct` payloads that appear in its signature (`IClock`/`UnityClock`/`ManualClock`,
`ContactOutcome`/`ContactContext`/`ICollisionResolver`). Splitting those into 3–6 files each produced a
file tree where the meaningful unit was hard to find and every signature change touched several files.

## Decision

A file is named after its **primary type** and may contain that type's contract unit:

- the interface,
- its implementations (including test doubles),
- the enums and value types that appear in its signature,
- or a family of sibling strategy implementations (`StandardObstacleBehaviours.cs`).

Substantial services, `MonoBehaviour`s and `ScriptableObject` definitions still get their own file.

## Consequences

**Positive:** a reader opens one file to understand a whole seam; signature changes stay in one diff;
the tree stays navigable as content grows.

**Trade-off:** file-level grep for a type name is less precise; mitigated by keeping the primary type's
name equal to the file name and by IDE symbol navigation.

## Alternatives rejected

- **Strict one-type-per-file:** ~40% more files for zero architectural benefit.
- **One file per assembly:** loses all navigability.

## Review trigger

If a contract-unit file exceeds ~400 lines, split it — size, not type count, is the real constraint.
