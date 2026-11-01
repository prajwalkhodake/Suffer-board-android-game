# ADR-0015 — Support surfaces: the sensor declares, the motor interprets

- **Status:** Accepted
- **Date:** 2026-11-01
- **Contract:** `TECH_DOC.md` §4.4, §4.7 (revision 1.0.2)
- **Refines:** ADR-0006 (which fixed contact *outcomes* but said nothing about vertical *support*)

## Context

The spec requires ramp-to-rooftop traversal: launch off a ramp, land on a train roof or a building top, run
along it, jump off it. ADR-0006 made collision outcomes deterministic but never addressed what the player
stands on — the motor integrated against `y = 0`, and the only component that knew a surface existed was the
contact sensor, which reports *contacts*, not *standing*.

The obvious fixes were both wrong:

- giving the motor its own raycast puts `Physics` reads into the locomotion module and re-introduces exactly
  the frame-rate coupling ADR-0006 was written to remove;
- letting the motor hold a list of mountable obstacles makes locomotion depend on obstacle identity, which
  breaks §4.4's "it does not decide collisions" boundary.

Two real defects appeared while implementing this, and both shaped the contract:

1. The landing decision was being taken from a **rendered** frame, so at 30 FPS a thin roof could be skipped
   entirely and the rig fell through, while at 120 FPS the same jump landed. Frame-rate-dependent gameplay
   truth is the failure mode §11.2 rule 2 prohibits.
2. Losing the surface **snapped** the rig to the ground plane, which read as a teleport rather than a drop.

## Decision

1. `IPlayerMotor` gains `GroundHeight` (the top of the surface the rig is supported over; `0` is the ground
   plane) and `SetSupportHeight(float metres)`. **The sensor declares, the motor interprets.** The contact
   sensor — the only system that reads physics — passes a single number, chosen as the highest mountable
   surface currently overlapping the rig. The motor alone decides what that means: land on it, step up onto
   it, or fall off its far edge. The motor never learns what an obstacle *is*, and no `IObstacleBehaviour` is
   consulted on the vertical path.
2. `HeightAboveGround` stays measured from the **world ground plane**, not from the support surface. Clearance
   maths therefore remains absolute, and a roof cannot fool a clearance check: a player standing on a train top
   is high, which is exactly why a low barrier no longer blocks them. §4.7 keeps a single definition of "how
   high is the player" for every behaviour.
3. Landing, step-up and fall-off are resolved from the **simulated step**, never from a rendered frame, so
   rooftop support is identical at 30 and 120 FPS. The motor's vertical motion was already closed-form
   (gravity derived from authored height and air time); support joins the same integration rather than
   bypassing it.
4. Losing the support surface is a **fall from rest**, not a snap to zero, so walking off a roof reads as a
   drop and gives the player time to react or to be caught by a lower surface.
5. The motor has no `Update` and reads no clock. Every vertical input arrives through `Tick(dt)` plus the
   three explicit entry points `SetSupportHeight`, `NotifyStumble` and `SetInvulnerable`, so the run
   controller remains the only simulation owner (§4.12) and a test can drive support directly.

## Consequences

### Positive

Rooftop traversal required **no** change to the collision resolver, to the obstacle behaviours or to the
generator: the seam is the only new contract, so ramp/roof content stays data plus a sensor. Multi-roof
selection (which of several overlapping tops the rig is on, and what becomes the support when the current one
is exited) lives entirely inside the sensor's mount table, so a future moving roof cannot affect locomotion.
Vertical state is reproducible from a seed and drivable at any cadence in tests, which is what makes the
height assertions in the greybox suite meaningful rather than decorative.

### Trade-off

Two systems now share one vertical truth, so a support bug can originate on either side. Mitigated by the
ordering being strictly one-way (sensor → motor → nobody) and by `PlayerMotor` containing no physics reads, so
support behaviour can be asserted in isolation by calling `SetSupportHeight`. `GroundHeight` and
`HeightAboveGround` are easy to confuse; §4.4 labels them *support* versus *clearance* and names the sensor as
the single writer of the former. The sensor now owns two responsibilities — reporting contacts and declaring
support — which is acceptable precisely because both are "what does physics say", and neither is "what does
this object mean".

## Alternatives rejected

- **Motor raycasts down each frame:** physics in the locomotion module, one cast per frame forever, and
  support becomes frame-rate dependent again.
- **Motor holds a list of mountable obstacles:** couples locomotion to obstacle identity; every new mountable
  kind would then touch the motor.
- **`OnTriggerStay`-driven support:** Unity's callback cadence is not the simulation step, so it inherits
  defect 1 above.
- **Snap to ground when the surface is lost:** one line cheaper, visibly wrong, and it hides fall bugs from
  playtesters.

## Review trigger

- If a third system must influence vertical state (a hovering board, an elevator set piece, a moving roof that
  carries the rig), it enters as a *contribution* through the existing modifier pattern, or as another
  declarator calling `SetSupportHeight`. It does not get to integrate the motor.
- If support must become per-lane (a roof covering only one lane), the declaration becomes a (height, lane)
  pair and the sensor still owns the read — do not move lane tests into the motor.
- `CrashCause.FellOutOfWorld` is reserved and **unproduced** in M1: today no chunk can open a real gap under
  the player, so a fall always terminates on a surface. The first chunk that authors a bottomless drop needs
  a detector (sensor-side, publishing a crash) — at which point §4.7 must state who owns the world-floor test.
