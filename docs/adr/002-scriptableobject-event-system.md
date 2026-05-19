# ADR 002 — Event System: ScriptableObject GameEvents

**Status:** Accepted  
**Date:** 2026-05-19

## Context

Systems need to communicate state changes (player died, wave ended, score changed) without creating direct references between unrelated classes. A mobile game at scale needs this decoupling to allow parallel development across teams.

## Decision

Use **ScriptableObject-based GameEvents** (`com.madratzz.scriptableobject.eventsystem.*`).

## Rationale

- Events are assets — they appear in the Project window, can be inspected, and can be fired from the Editor at runtime for testing.
- No code coupling: producers and consumers both reference a shared asset, not each other.
- `GameEvent` (no args) and `GameEventWithInt` (typed payload) cover 90% of cases without a reactive framework.
- Integrates with VContainer (events registered as instances).

## Consequences

- Every event must be a named ScriptableObject asset, discoverable in the Project window under `Core/Events/`.
- Events are single-typed — complex payloads should use a context ScriptableObject or a struct reference, not multiple event parameters.
- Subscribers must unsubscribe in `OnDestroy` / `Exit` — event assets persist across scenes.

## Alternatives Rejected

- **UniRx / R3:** Powerful but adds a reactive programming model that is non-trivial to onboard 50+ engineers onto. Harder to debug.
- **C# events / delegates directly:** Creates hard references between assemblies, defeats decoupling.
- **Unity's built-in UnityEvent:** Inspector-wired, no editor fire support, harder to serialize.
