# Unity Architecture Audit — Asteroids Demo
**Prepared for:** Scale to 50+ Engineers on a Large-Scale Unity Mobile Game  
**Audited branch:** `claude/audit-unity-architecture-VYrAh`  
**Date:** 2026-05-19

---

## Executive Summary

The codebase demonstrates a strong architectural foundation: VContainer dependency injection with scoped lifetimes, ScriptableObject-driven events and variables, feature-folder organization, pure logic classes separated from MonoBehaviours, object pooling via Addressables, and meaningful test coverage of pure logic. These patterns are the right instincts for a scalable game.

However, several structural gaps will become severe blockers as team size grows past ~10 engineers. The most critical is that **all game code lives in a single assembly definition** (`Project.Runtime`), meaning every change recompiles the entire codebase and nothing enforces module boundaries. Combined with a handful of dependency-direction violations (Core importing Feature types), runtime state on ScriptableObject assets, and the absence of team-process infrastructure (editorconfig, CODEOWNERS, coding standards), the codebase needs targeted structural work before headcount scales.

The issues below are prioritized. Fix the **Critical** tier before adding engineers; address **High** tier before the first production milestone.

---

## Dependency Graph (Current State)

```
Core (RootLifetimeScope)
  └── ❌ directly imports WaveSettingsSO, AsteroidSettingsSO (Feature types)

NormalGameState (GamePlay, ScriptableObject asset)
  └── ❌ holds runtime fields: _gameplayScope, _playerInstance, _waveSpawnerInstance
  └── ❌ calls LifetimeScope.Find<RootLifetimeScope>() (service locator)

PlayerController
  └── ❌ [SerializeField] InputSystemReader (concrete) instead of interface

WaveSpawner.Update()
  └── ❌ polls _activeAsteroidCount > 0 every frame

NormalGameState.Tick()
  └── ❌ polls PlayerLives <= 0 every frame

Project.Runtime.asmdef
  └── ❌ autoReferenced: true  (everything sees everything)
  └── ❌ single assembly for all ~60 scripts
```

---

## Critical Issues

### C1 — Single Monolithic Assembly (All 60 scripts in `Project.Runtime`)

**File:** `Assets/_Game/Project.Runtime.asmdef`

Every change to any script recompiles the entire game. With 50+ engineers committing simultaneously, this creates constant compile storms and makes it impossible to detect or prevent cross-module coupling.

`autoReferenced: true` makes it worse: any new script added anywhere in `Assets/` automatically joins the assembly with zero explicit dependency declaration.

**Target assembly structure:**

```
Project.Core.Interfaces        (IDamageable, IFlowLogic, IPool — pure C#, no Unity)
Project.Core.Runtime           (ApplicationBase, FlowController, Pooling, VContainer scopes)
Project.Feature.ScreenWrap     (ScreenWrap, ScreenWrapLogic)
Project.Feature.InputReader    (InputSystemReader, GameControls)
Project.Feature.Weapons        (WeaponController, WeaponLogic, Projectile, ProjectilePool)
Project.Feature.Asteroids      (Asteroid, AsteroidPool, AsteroidSettingsSO)
Project.Feature.Waves          (WaveLogic, WaveSpawner, WaveSpawnerLogic, WaveSettingsSO)
Project.Feature.Player         (PlayerController, PlayerMotor, PlayerSettingsSO, IPlayerInput)
Project.GamePlay               (GameState, NormalGameState)
Project.UI                     (UIView, UIViewState, IntVariableBinder, all UI scripts)
Project.Tests.EditMode         (existing test assembly, updated references)
Project.Tests.PlayMode         (existing test assembly, updated references)
```

**Dependency rules (arrows = "depends on"):**

```
Core.Interfaces ← (nothing)
Core.Runtime ← Core.Interfaces
Feature.ScreenWrap ← Core.Interfaces
Feature.InputReader ← Core.Interfaces
Feature.Weapons ← Core.Runtime, Core.Interfaces
Feature.Asteroids ← Core.Runtime, Core.Interfaces, Feature.ScreenWrap
Feature.Waves ← Core.Runtime, Feature.Asteroids
Feature.Player ← Core.Interfaces, Feature.InputReader, Feature.Weapons, Feature.ScreenWrap
GamePlay ← Core.Runtime, Feature.Player, Feature.Asteroids, Feature.Waves
UI ← Core.Runtime, Core.Interfaces
```

**Draft asmdef files are provided in this PR as a starting point.** Each one sets `"autoReferenced": false` to enforce explicit dependency declarations. Migrate feature by feature: extract one assembly, fix any compile errors, repeat.

---

### C2 — Core Imports Feature Types (Dependency Direction Violation)

**File:** `Assets/_Game/Core/VContainer/RootLifetimeScope.cs:10-11`

```csharp
// WRONG — Core cannot know about Features
using ProjectGame.Features.Enemies;   // AsteroidSettingsSO
using ProjectGame.Features.Waves;     // WaveSettingsSO
```

Core is supposed to sit at the bottom of the dependency graph. It currently pulls Feature types upward, creating a circular coupling risk and preventing feature assemblies from being built independently.

**Fix:** Move feature-specific registrations to a `GameInstaller` at the GamePlay layer:

```csharp
// Assets/_Game/GamePlay/VContainer/GameLifetimeScope.cs
public class GameLifetimeScope : LifetimeScope
{
    [SerializeField] private WaveSettingsSO WaveSettings;
    [SerializeField] private AsteroidSettingsSO AsteroidSettings;

    protected override void Configure(IContainerBuilder builder)
    {
        builder.RegisterInstance(WaveSettings).AsSelf();
        builder.RegisterInstance(AsteroidSettings).AsSelf();
    }
}
```

`RootLifetimeScope` should register only truly global services (ApplicationFlowLogic, FiniteStateMachine, TimeMachineTick).

---

### C3 — ScriptableObject Assets Holding Runtime State

**Files:** `Assets/_Game/GamePlay/Scripts/NormalGameState.cs`, `GameState.cs`

`NormalGameState` and `GameState` are `ScriptableObject` assets (they live on disk), but they hold runtime object references:

```csharp
// NormalGameState.cs — these fields are on a disk asset
private LifetimeScope _gameplayScope;          // runtime object
private AsyncOperationHandle<GameObject> _playerHandle;   // runtime handle
private GameObject _playerInstance;           // runtime object
private WaveSpawner _waveSpawnerInstance;     // runtime object
```

`GameState.cs` has the same problem:
```csharp
// No [NonSerialized] — these structs WILL be serialized into the asset
protected AsyncOperationHandle<GameObject> _hudHandle;
protected AsyncOperationHandle<GameObject> _levelHandle;
```

In the editor, if Play mode exits abnormally the asset retains stale handles. In production, this is a subtle memory leak source.

**Fix — Runtime Context Object pattern:**

```csharp
// NormalGameState.cs
[CreateAssetMenu(...)]
public class NormalGameState : GameState
{
    // Only data/config here — nothing runtime
    [SerializeField] private AssetReferenceGameObject PlayerShipReference;
    // ...

    public override IEnumerator Execute()
    {
        var ctx = new NormalGameRuntimeContext(); // plain C# class, not an asset
        yield return ExecuteWithContext(ctx);
    }
}

internal class NormalGameRuntimeContext
{
    public LifetimeScope GameplayScope;
    public AsyncOperationHandle<GameObject> PlayerHandle;
    public GameObject PlayerInstance;
    public WaveSpawner WaveSpawnerInstance;
}
```

Also add `[NonSerialized]` to all runtime fields on `GameState` immediately as a stop-gap:

```csharp
[NonSerialized] protected AsyncOperationHandle<GameObject> _hudHandle;
[NonSerialized] protected AsyncOperationHandle<GameObject> _levelHandle;
```

---

### C4 — Service Locator Anti-Pattern in NormalGameState

**File:** `Assets/_Game/GamePlay/Scripts/NormalGameState.cs:65`

```csharp
// Hidden dependency — nothing in the signature says this needs a scene LifetimeScope
var rootScope = LifetimeScope.Find<RootLifetimeScope>();
```

`LifetimeScope.Find<T>()` is a static scene scan (service locator). It hides a mandatory runtime dependency, makes the class untestable in isolation, and fails silently if the scene isn't set up correctly.

**Fix:** Inject the parent scope. Since `NormalGameState` is a ScriptableObject, use a MonoBehaviour bootstrapper that receives the injection and passes it to the state:

```csharp
// GameStateBootstrapper.cs (MonoBehaviour in the scene)
public class GameStateBootstrapper : MonoBehaviour
{
    [SerializeField] private NormalGameState State;

    [Inject]
    public void Construct(RootLifetimeScope rootScope)
    {
        State.SetParentScope(rootScope);
    }
}
```

---

### C5 — Per-Frame Polling Instead of Events

**Files:** `NormalGameState.cs:53`, `WaveSpawner.cs:75`

```csharp
// NormalGameState.Tick() — called every frame
if (PlayerLives <= 0) GotoLevelFail.Invoke();

// WaveSpawner.Update() — every frame until wave ends
if (_isSpawning || _activeAsteroidCount > 0) return;
StartNextWave();
```

Both conditions are state changes that can be observed via the existing event system. Polling adds CPU overhead and obscures intent.

**Fix for PlayerLives:** Swap `Int` for `IntWithEvent` on `NormalGameState._playerLives` and subscribe:

```csharp
private void OnPlayerLivesChanged()
{
    if (PlayerLives.GetValue() <= 0)
        GotoLevelFail.Invoke();
}
```

**Fix for wave completion:** Fire a `GameEvent WaveCompleted` from `WaveSpawner.HandleSplit` when `_activeAsteroidCount` reaches zero, then subscribe `StartNextWave` to it.

---

### C6 — Internal Packages as Local Tarballs

**File:** `Packages/manifest.json`

All `com.madratzz.*` packages are local `.tgz` files checked into `ThirdPartyPackages/`:

```json
"com.madratzz.scriptableobject.architecture": "file:../ThirdPartyPackages/com.madratzz.scriptableobject.architecture-0.0.4.tgz"
```

With 50+ engineers: no package versioning server means version drift between workstations, packages can't be updated safely, and CI will embed binaries in git history.

**Fix:** Host packages on a scoped npm registry (Verdaccio self-hosted, or GitHub Packages) and reference them by semver:

```json
"com.madratzz.scriptableobject.architecture": "0.0.4"
```

Add a `scopedRegistries` entry to `manifest.json` pointing to the registry URL. Until then, at minimum lock each package to a specific git tag instead of a tarball.

Also: `jp.hadashikick.vcontainer` is pulled from git HEAD (no tag, no commit hash) — a non-deterministic dependency that can silently change between CI builds:

```json
// WRONG
"jp.hadashikick.vcontainer": "https://github.com/hadashiA/VContainer.git?path=VContainer/Assets/VContainer"

// CORRECT — pin to a tag
"jp.hadashikick.vcontainer": "https://github.com/hadashiA/VContainer.git?path=VContainer/Assets/VContainer#1.16.6"
```

---

## High Priority

### H1 — No Per-Feature Test Coverage

Only 4 pure-logic classes have unit tests (WeaponLogic, ProjectileLogic, ScreenWrapLogic, ApplicationFlowLogic). The following have zero coverage:

| Untested Class | Risk |
|---|---|
| `WaveLogic` | Wave count/speed formulas directly affect game balance |
| `WaveSpawnerLogic` | Edge spawn positions affect perceived fairness |
| `AsteroidSettingsSO.TryGetSplitRule` | Split logic is the core game mechanic |
| `ApplicationFlowController` | Route mismatches cause silent dead-ends |
| `PlayerController.RespawnRoutine` | Lives decrement logic is business-critical |

For 50+ engineers, enforce a minimum coverage gate in CI. Each new logic class ships with tests.

---

### H2 — No Feature Flag System

New features shipped by any of 50 engineers go live immediately on deploy. Without feature flags there is no mechanism for staged rollouts, kill switches, or A/B testing.

**Recommended approach:** A `FeatureFlagSO` ScriptableObject that maps flag names to booleans, with a remote config override layer (Firebase Remote Config or similar):

```csharp
[CreateAssetMenu(menuName = "Core/Feature Flag")]
public class FeatureFlagSO : ScriptableObject
{
    [SerializeField] private string FlagKey;
    [SerializeField] private bool DefaultValue;

    public bool IsEnabled => RemoteConfig.GetBool(FlagKey, DefaultValue);
}
```

---

### H3 — No Analytics Architecture

Mobile games depend on analytics from launch day. There is no analytics infrastructure — no event definitions, no provider abstraction, no session tracking.

**Minimum viable architecture:**

```csharp
// IAnalyticsService.cs (in Core.Interfaces assembly)
public interface IAnalyticsService
{
    void LogEvent(string eventName, Dictionary<string, object> parameters = null);
    void LogLevelStart(int wave, int lives);
    void LogLevelFail(int wave, int score);
}
```

Wire into the existing GameEvent system: subscribe analytics calls to `GameStateStart`, `GotoLevelFail`, `EnemyDestroyed`. Concrete implementations (Firebase, Unity Analytics, custom) are swapped via VContainer without touching game code.

---

### H4 — No Save / Persistence Layer

`PlayerLives` and `CurrentPlayerScore` are in-memory ScriptableObject variables. Nothing persists across sessions. For a mobile game this means no progression, no leaderboards, no settings persistence.

**Design:** An `IDataRepository` interface with platform implementations:

```csharp
public interface IDataRepository
{
    Task<T> LoadAsync<T>(string key) where T : new();
    Task SaveAsync<T>(string key, T data);
}
```

Use `PlayerPrefs` as a local fallback, cloud save (Unity Gaming Services or custom) for cross-device. Register via VContainer so game code never references a concrete storage type.

---

### H5 — No Crash Reporting / Error Telemetry

`Debug.LogError` is the only error path. In production it is silent. For a mobile game at scale, implement an `IErrorReporter` interface early:

```csharp
public interface IErrorReporter
{
    void ReportException(Exception ex, Dictionary<string, string> context = null);
    void ReportError(string message, Dictionary<string, string> context = null);
}
```

Integrate Firebase Crashlytics or Sentry as the concrete implementation. Replace all critical `Debug.LogError` calls with `_errorReporter.ReportError(...)`.

---

### H6 — UI Architecture: No Navigation Service

`ApplicationFlowController` hardcodes every navigation route as a `[SerializeField] Transition`. Adding a new screen requires modifying this MonoBehaviour, wiring new inspector references, and retesting all existing routes.

For a large team building many screens, introduce an `INavigationService`:

```csharp
public interface INavigationService
{
    void GoTo(ScreenId screen, NavigationParams parameters = null);
    void GoBack();
    void ShowModal(ScreenId modal);
    void DismissModal();
}
```

Screen registrations live in a `NavigationManifestSO` (ScriptableObject dictionary of `ScreenId → AssetReference`). This means any engineer can add a screen without touching the core navigation controller.

---

### H7 — No Addressables Labels / DLC Strategy

Addressable groups have no documented labeling strategy. For mobile:
- Labels should distinguish `startup` (required at launch) from `optional` (streamed)
- Platform-specific variants need their own labels
- Remote content catalog URL must be environment-configurable (dev/staging/prod)

Without this, the first attempt at asset streaming or DLC will require a full Addressables restructure.

**Minimum label taxonomy:**
```
startup          — loaded before first frame
gameplay-core    — loaded on game scene entry
ui               — all UI prefabs
audio            — audio clips
vfx              — particle/VFX assets
remote           — content delivered via CDN (not in base build)
```

---

### H8 — Addressables `com.unity.addressables` Package Missing

`Packages/manifest.json` uses `Addressables` throughout the codebase but the `com.unity.addressables` package is not listed as an explicit dependency. It may be pulled in transitively. Pin it explicitly so updates are intentional:

```json
"com.unity.addressables": "2.3.1"
```

---

### H9 — No Profiler Markers on Hot Paths

`WaveSpawner.Update()`, `ObjectPoolBase`, and physics callbacks have no `ProfilerMarker` instrumentation. With 50+ engineers it becomes impossible to attribute CPU budget spikes to specific systems.

```csharp
private static readonly ProfilerMarker s_WaveUpdateMarker =
    new ProfilerMarker("WaveSpawner.Update");

private void Update()
{
    using (s_WaveUpdateMarker.Auto())
    {
        // existing code
    }
}
```

Add markers to: `WaveSpawner.Update`, `ObjectPoolBase.Get/Release`, `ScreenWrap.Update`, and all `OnTriggerEnter2D` callbacks.

---

## Medium Priority

### M1 — No Coding Standards Enforcement

No `.editorconfig`, no Roslyn analyzer configuration, no style guide. With 50+ engineers this produces inconsistent code that is hard to review and merge.

**`.editorconfig` is included in this PR** (`/.editorconfig`). It enforces: 4-space indentation, LF line endings, `var` preference, brace style, namespace declarations. Add the `Microsoft.Unity.Analyzers` NuGet analyzer to `Assets/` to surface Unity-specific code quality issues in IDEs.

---

### M2 — No Module Ownership (CODEOWNERS)

No `.github/CODEOWNERS` file means any engineer can modify any system without the owning team being notified.

**Sample `.github/CODEOWNERS`:**
```
# Core systems — Platform team owns
/Assets/_Game/Core/                  @platform-team
/Assets/_Game/GamePlay/              @platform-team

# Gameplay features — Gameplay team owns
/Assets/_Game/Features/Player/       @gameplay-team
/Assets/_Game/Features/Asteroids/    @gameplay-team
/Assets/_Game/Features/Waves/        @gameplay-team
/Assets/_Game/Features/Weapons/      @gameplay-team

# UI — UI team owns
/Assets/_Game/UI/                    @ui-team

# Packages — Package owners required on all manifest changes
/Packages/manifest.json              @platform-team @tech-lead
```

---

### M3 — Scene Merge Conflict Risk

With 50+ engineers, Unity scene files (YAML) are merge conflict magnets. Currently `GameScene.unity` likely contains direct references to gameplay objects that multiple teams will modify.

**Mitigation:**
1. Minimize GameObject count in scenes — load everything via Addressables at runtime (this project mostly does this already; ensure nothing regresses)
2. Split scenes by ownership boundary: `GameScene_Lighting.unity`, `GameScene_UI.unity` etc., loaded additively
3. Use Prefab overrides instead of scene overrides wherever possible
4. Enable **Force Text** serialization mode (`Edit → Project Settings → Editor → Asset Serialization`)

---

### M4 — `PlayerController` References Concrete `InputSystemReader`

**File:** `Assets/_Game/Features/Player/Scripts/PlayerController.cs:13`

```csharp
[SerializeField] private InputSystemReader InputReader;  // concrete type
```

`PlayerMotor` and `WeaponController` correctly use `IPlayerInput`. `PlayerController` breaks this pattern by serializing the concrete implementation. This prevents swapping the input backend (e.g., for replay systems, AI-controlled players, or automated tests) without modifying the inspector.

Unity can't serialize interfaces directly, so use a MonoBehaviour adapter:

```csharp
[SerializeField] private MonoBehaviour InputReaderComponent;
private IPlayerInput _input;

private void Awake()
{
    _input = InputReaderComponent as IPlayerInput;
    // ...
}
```

---

### M5 — Coroutine State Machine: No Cancellation Support

The state machine uses `IEnumerator` coroutines for `Init`, `Execute`, `Tick`, `Pause`, `Resume`, `Exit`. Coroutines cannot be cleanly cancelled mid-execution (only stopped from outside via `StopCoroutine`). This makes it hard to:
- Handle rapid state transitions (transition requested while `Execute` is still loading assets)
- Test state logic in isolation
- Compose states (run two states in parallel)

**Recommended migration:** Replace `IEnumerator` with `UniTask` (Cysharp). CancellationToken support makes interruption explicit and safe:

```csharp
public override async UniTask Execute(CancellationToken ct)
{
    await LoadPlayerAsync(ct);      // ct.ThrowIfCancellationRequested()
    await LoadSpawnerAsync(ct);
    GameStateStart.Invoke();
}
```

---

### M6 — `com.unity.visualscripting` Included but Unused

**File:** `Packages/manifest.json`

Visual scripting is listed as a dependency but there is no visual scripting in the project. Unused packages add compile time, editor overhead, and binary bloat.

Remove: `com.unity.visualscripting`, `com.unity.multiplayer.center` (unless multiplayer is actively planned).

---

### M7 — `WaveSpawner.DebugKillAll()` Uses `FindObjectsByType` 

**File:** `Assets/_Game/Features/Waves/Scripts/WaveSpawner.cs:119`

```csharp
var allAsteroids = FindObjectsByType<Asteroid>(FindObjectsSortMode.None);
```

`FindObjectsByType` is an O(n) scene scan. This is in a `[Button]` debug method so it won't run in production, but the pattern should not spread — future engineers will copy it. The `WaveSpawner` already has `_poolMap`; track active asteroids via pool callbacks instead.

---

### M8 — No Architecture Decision Records (ADRs)

Why VContainer over Zenject? Why ScriptableObject events over UniRx/R3? Why coroutine state machine over a library? New engineers can't understand these choices and will make inconsistent decisions.

Create `docs/adr/` with lightweight ADR files:
```
docs/adr/
  001-dependency-injection-vcontainer.md
  002-scriptableobject-event-system.md
  003-addressables-for-asset-loading.md
  004-pure-logic-separated-from-monobehaviour.md
```

---

### M9 — No CI/CD Integration Points

No `.github/workflows` for automated build validation or test gates. With 50 engineers merging daily:
- Broken builds should be caught before merge
- Test suite should run on every PR
- Platform builds (Android/iOS) should be triggered on merge to `main`

**Minimum CI pipeline:**
```yaml
# .github/workflows/test.yml
- name: Run Edit Mode Tests
  uses: game-ci/unity-test-runner@v4
  with:
    testMode: editmode

- name: Run Play Mode Tests  
  uses: game-ci/unity-test-runner@v4
  with:
    testMode: playmode
```

---

## Low Priority / Future Considerations

### L1 — No Localization System
No L10n infrastructure. Add `com.unity.localization` early — retrofitting it into shipped UI is expensive.

### L2 — No Audio Architecture
No audio manager, no audio pooling, no music state machine. Needed before content production begins.

### L3 — No Accessibility Layer
No font scaling, no colorblind modes, no screen reader support. Mobile storefronts increasingly require this.

### L4 — No Remote Configuration
All tuning values (spawn rates, speeds, wave counts) are baked into ScriptableObjects at build time. Integrate remote config (Firebase Remote Config) so live-ops can tune without a build.

### L5 — DOTween in `Assets/Plugins` (Not a Package)
DOTween is vendored as a plugin rather than managed via Package Manager. Harder to update, no version pinning. Switch to DOTween PRO via Package Manager or to Unity's own animation tooling.

---

## Recommended Prioritization by Sprint

| Sprint | Action |
|--------|--------|
| **Now** | Fix C3 (`[NonSerialized]` on async handles), pin VContainer to a git tag (C6), remove unused packages (M6) |
| **Sprint 1** | Per-feature assembly definitions (C1), fix Core→Feature dependency (C2), add `.editorconfig` + CODEOWNERS |
| **Sprint 2** | Replace polling with events (C5), fix service locator (C4), analytics interface (H3) |
| **Sprint 3** | Save/persistence layer (H4), crash reporting (H5), navigation service (H6) |
| **Sprint 4** | Feature flag system (H2), Addressables labeling strategy (H7), CI pipeline (M9) |
| **Backlog** | UniTask migration (M5), ADRs (M8), localization (L1), audio architecture (L2) |

---

## What the Codebase Gets Right

These patterns should be preserved and extended to new systems:

- **VContainer with scoped lifetimes** — The `rootScope → gameplayScope` child scope pattern is excellent. Gameplay objects are cleanly created and disposed per session. Extend this to feature-level scopes.
- **Pure logic classes** — `WeaponLogic`, `ScreenWrapLogic`, `WaveLogic`, `ApplicationFlowLogic` are plain C# with no Unity dependency. This is the right pattern; enforce it for all new business logic.
- **ScriptableObject events and variables** — Loose coupling without a heavyweight reactive framework. The `IntWithEvent` pattern (value + change notification) is solid. Extend it to replace the remaining polling cases.
- **Object pooling via Addressables** — `ObjectPoolBase<T>` is clean and generic. The async-load-then-pool pattern is correct for mobile memory management.
- **Feature-folder organization** — `Features/Player`, `Features/Weapons`, etc. maps cleanly to team ownership. Formalize this with per-feature assemblies.
- **Command pattern in ApplicationFlowController** — The `Dictionary<FlowIntent, Action>` dispatch is clean and trivially extensible.
- **Test coverage on pure logic** — The existing edit-mode tests are well-structured. The pattern of "test the logic class, not the MonoBehaviour" should be the team standard.
