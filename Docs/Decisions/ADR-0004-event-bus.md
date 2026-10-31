# ADR-0004 — Struct-based event bus as the only cross-module signal

- **Status:** Accepted
- **Date:** 2026-10-31
- **Contract:** `TECH_DOC.md` §4.0, §4.14, §10

## Context

Modules must react to each other without referencing each other (ADR-0002): missions need to know a
coin was collected, analytics needs crash causes, audio needs speed, HUD needs score. Direct calls
would create the very coupling the module split forbids; UnityEvents and `SendMessage` are slow,
untyped and unhookable in code review.

## Decision

`Skyrift.Core` owns `IEventBus` with a **struct** payload generic API:

```csharp
void Subscribe<T>(Action<T> handler) where T : struct, IGameEvent;
void Unsubscribe<T>(Action<T> handler);
void Publish<T>(in T evt) where T : struct, IGameEvent;
```

- Events are `readonly struct`s named `<System><PastTense>Event`; no reference-type payloads, no
  `object` boxing, no LINQ in handlers.
- Two buses: domain (gameplay → meta/services/audio) and UI (systems → views). UI never publishes on
  the domain bus except through commands.
- Dispatch uses a pre-sized handler array and an index loop; publishing during dispatch is queued and
  drained after the current pass, so a handler can safely publish.
- A debug build records publish counts per event type (`EventBusProfiler`) to catch hidden per-frame
  chatter.

## Consequences

**Positive:** complete decoupling (missions/analytics become a *subscriber*, not a caller), zero
allocation dispatch, trivially unit-testable (subscribe in test, assert), and a single place to trace
"who reacts to X". New features hook into existing signals with no edits to publishers.

**Trade-off:** control flow is asynchronous and implicit; debugging requires the profiler window.
Mitigation: every event type is documented in one registry and handlers must be registered in an
`OnInit`/`OnDispose` pair (enforced by a `EventSubscriptionGuard` test in PlayMode).

## Alternatives rejected

- **Direct interface calls:** reintroduces coupling; missions would have to be referenced by gameplay.
- **`UnityEvent` / C# reference-type events:** allocate, no compile-time payload contract, hard to test.
- **Message broker package:** unnecessary dependency for <100 event types.

## Review trigger

If event traffic exceeds the debug profiler budget on device, split the bus per domain (run-time vs
meta) before adding any throttling.
