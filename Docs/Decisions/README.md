# Architecture Decision Records

One short record per decision that is expensive to reverse. Append-only: when a decision changes, write a
new ADR that **supersedes** the old one and mark the old one `Superseded by ADR-NNNN`. Never delete.

Template:

```markdown
# ADR-NNNN — Title

- **Status:** Proposed | Accepted | Superseded by ADR-NNNN | Deprecated
- **Date:** YYYY-MM-DD
- **Contract:** TECH_DOC.md §x.y
- **Supersedes:** —
- **Refined by:** —   (optional: a later ADR that narrows part of this decision without superseding it)

## Context
## Decision
## Consequences
### Positive
### Trade-off
## Alternatives rejected
## Review trigger
```

## Index

| ADR | Title | Status | Contract |
|---|---|---|---|
| [0001](ADR-0001-engine-and-platform-baseline.md) | Unity 2022.3 LTS + URP, IL2CPP/ARM64, portrait Android | Accepted | §2.1, §2.3 |
| [0002](ADR-0002-assembly-modularity.md) | One assembly per module with an enforced acyclic DAG | Accepted | §3.2, §3.3 |
| [0003](ADR-0003-service-container.md) | In-house service container instead of a DI framework | Accepted | §4.0, §4.1 |
| [0004](ADR-0004-event-bus.md) | Struct-based event bus as the only cross-module signal | Accepted | §4.0, §4.14, §10 |
| [0005](ADR-0005-chunk-based-track-generation.md) | Hand-authored chunks + seeded weighted generator | Accepted | §4.5, §5.4 |
| [0006](ADR-0006-deterministic-collision.md) | Deterministic physics-free collision, two-hit stumble | Accepted | §4.7 |
| [0007](ADR-0007-pooling-and-floating-origin.md) | Pooling only, plus recycling and floating origin | Accepted | §4.6, §11.2 |
| [0008](ADR-0008-powerup-engine.md) | Power-up rules in data, mechanics in behaviours | Accepted | §4.10, §5.8 |
| [0009](ADR-0009-save-system.md) | Sliced, versioned, encrypted save + migration chain | Accepted | §4.15, §5.15, §8 |
| [0010](ADR-0010-cloud-conflict-resolution.md) | Cloud conflict: revision + baseline, authoritative slices | Accepted | §4.17.1, §5.15 |
| [0011](ADR-0011-remote-config-and-feature-flags.md) | Typed RC keys with defaults + feature flags | Accepted | §4.16, §9 |
| [0012](ADR-0012-addressables-and-asset-delivery.md) | Addressables everywhere + Play Asset Delivery | Accepted | §2.3, §4.20, §11.1 |
| [0013](ADR-0013-contract-unit-files.md) | Contract-unit files instead of strict one-type-per-file | Accepted | §13.2 |
| [0014](ADR-0014-pool-keys-and-origin-rebasing.md) | String pool keys, discrete origin roots, run distance as a scalar | Accepted (refines 0007) | §4.4, §4.5, §4.6, §15 |
| [0015](ADR-0015-support-surface-seam.md) | Support surfaces: the sensor declares, the motor interprets | Accepted (refines 0006) | §4.4, §4.7 |
