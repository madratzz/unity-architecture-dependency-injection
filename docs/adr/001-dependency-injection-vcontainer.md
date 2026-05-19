# ADR 001 — Dependency Injection: VContainer

**Status:** Accepted  
**Date:** 2026-05-19

## Context

The project needs a dependency injection framework to decouple system initialization from system usage, support scoped lifetimes (global singletons vs per-level objects), and make logic classes testable without MonoBehaviour scaffolding.

## Decision

Use **VContainer** (`jp.hadashikick.vcontainer`).

## Rationale

| Criterion | VContainer | Zenject |
|---|---|---|
| Allocation overhead | Near-zero (struct-based resolvers) | Higher (class-based) |
| Compile-time safety | Yes (source generator mode) | Partial |
| Unity lifecycle integration | First-class (`IStartable`, `ITickable`) | Via MonoInstaller |
| Package manager support | Yes (UPM via git) | Manual |
| Active maintenance | Yes | Slower cadence |

VContainer's child scope API (`LifetimeScope.CreateChild`) maps cleanly to the session lifecycle pattern used here: global services in `RootLifetimeScope`, level-specific services in a child scope that is disposed on level exit.

## Consequences

- All cross-system wiring must go through VContainer — no `GetComponent` between unrelated systems, no singletons.
- `LifetimeScope.Find<T>()` (static scene scanning) is **prohibited** — it defeats injection. Use injected parent scopes instead.
- Feature-level ScriptableObject states that need DI must receive dependencies via a MonoBehaviour bootstrapper, not via static lookup.

## Alternatives Rejected

- **Zenject:** Heavier, slower resolve, less active. Migration cost not justified.
- **Manual service locator:** No compile-time safety, hidden dependencies, untestable.
- **No DI:** Not viable at 50+ engineer scale.
