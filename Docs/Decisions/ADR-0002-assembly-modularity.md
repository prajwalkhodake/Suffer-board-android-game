# ADR-0002 — One assembly per module, with an enforced acyclic dependency DAG

- **Status:** Accepted
- **Date:** 2026-10-31
- **Contract:** `TECH_DOC.md` §3.2, §3.3

## Context

A single `Assembly-CSharp` makes compile times grow linearly with content, allows any type to reach
any other, and hides architecture violations until they are expensive. We want the compiler itself to
enforce "gameplay may not see UI" and "data may not see services".

## Decision

Split runtime code into 15 assemblies (`Skyrift.Core`, `.Data`, `.Services.Abstractions`,
`.Services`, `.Audio`, `.Controls`, `.Track`, `.Economy`, `.Meta`, `.Gameplay`, `.UI`,
`.Bootstrap`, `.Editor`, plus two test assemblies). Each assembly declares its references in `.asmdef`.
Only the arrows listed in `TECH_DOC.md` §3.3 are legal.

Enforcement is threefold: asmdef references (compile-time), an editor test that asserts the expected
reference set per asmdef, and the PR checklist.

`Gameplay` never references `UI` or `Services.*`; `UI` never references `Services.Runtime`;
`Data` references nothing above `Core`; `Bootstrap` is the only composition root.

## Consequences

**Positive:** compile times stay flat as content grows (content-heavy assemblies change rarely);
violations become compile errors instead of review debates; gameplay is testable headless (PlayMode
tests bootstrap without UI or SDKs).

**Trade-off:** more ceremony (new file ⇒ decide the assembly), and cross-cutting calls need an event or
an abstraction rather than a direct reference. That friction is the point.

## Alternatives rejected

- **Single assembly:** fast to start, unmaintainable at 200+ classes, no enforcement.
- **Folder-only separation:** convention decays; nothing fails when someone adds a wrong `using`.
- **Per-feature assemblies (one per power-up etc.):** too many assemblies, slow domain reload, no benefit.

## Review trigger

If a legitimate feature needs an arrow not in the DAG, add an ADR + interface rather than a reference.
