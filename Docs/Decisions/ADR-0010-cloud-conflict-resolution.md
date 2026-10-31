# ADR-0010 — Cloud save conflict policy: revision + baseline ticks, authoritative vs merged slices

- **Status:** Accepted
- **Date:** 2026-10-31
- **Contract:** `TECH_DOC.md` §4.17.1, §5.15

## Context

Players use two devices offline. On reconnect both copies are "valid" and neither is obviously newer.
Naive `last write wins` loses purchases when a phone with a stale clock syncs. Naive `merge everything`
duplicates currency (players grant themselves coins twice by playing offline on both devices).

## Decision

Every save carries `schemaVersion`, `revision` (monotonic per local write), `updatedAtUtcTicks` and
`deviceIdHash`. On boot:

1. Migrate both local and remote to the current schema version **before** any comparison.
2. Remote wins only if `remote.revision > local.revision` **and** `remote.updatedAtUtcTicks >
   local.syncBaselineTicks` (the ticks of the last successful sync — this defeats clock skew).
3. Otherwise local wins and is pushed with `revision + 1`.
4. After choosing the winner, **only additive slices merge with max()**: `stats`, `progression`.
   `economy` and `ownership` are authoritative — never merged, always taken from the winner.
5. Every resolution logs `cloud_sync_result{winner, reason}` so support can reconstruct what happened.
6. A queued offline push never increments revision retroactively; it is re-evaluated on reconnect.

## Consequences

**Positive:** no currency duplication (the exploitable path is closed by construction), no lost purchases
under normal play, offline play stays first-class, and the decision is explainable to a player or a
support agent from two numbers.

**Trade-off:** a player who advances on device B without syncing can lose device A's newer-looking
progress if revisions are equal; mitigated by the baseline-ticks rule and by the visible "last synced"
label. Achievements and stats are max-merged, so those never regress.

## Alternatives rejected

- **Pure last-write-wins on the client clock:** exploitable and wrong on skewed clocks.
- **Full CRDT merge:** correct but far too complex for eight slices, and still must not merge currency.
- **Server-authoritative state:** would remove the problem entirely but requires an authoritative backend
  and always-online play; revisit if the economy becomes server-side (see ROADMAP open question 1).

## Review trigger

If we introduce cross-device live sessions or server-authoritative economy, replace `ICloudMergeStrategy`.
