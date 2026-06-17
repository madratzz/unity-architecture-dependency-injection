# Code Review: DI Migration from ScriptableObject Architecture

**Repo:** `unity-architecture-dependency-injection`
**Date:** 2026-06-17
**Reviewer:** Hermes (acting on the user's request)
**Scope:** Full repo (63 .cs files, 30 .asset files, 14 packages in `manifest.json`)

## Goal

Migrate from a **ScriptableObject-as-pub-sub / ScriptableObject-as-reactive-state** pattern (`scriptableobject.architecture` package family) to a **VContainer DI** pattern. Keep SOs only for *designer-tunable static data*, replace them with POCO services for everything else.

---

## TL;DR

You have a working DI skeleton, but the rest of the game is still wired through ScriptableObject `GameEvent`/`Int`/`Float`/`DBInt` globals. The two systems are stitched together awkwardly, and the seams are leaking in 7 different places. A clean migration plan: **own the contracts, register pure C# services, and make SO assets pure data only** (or remove them entirely from the runtime path).

The good news: most of your game logic is already split into POCO "logic" classes (`WaveLogic`, `WaveSpawnerLogic`, `WeaponLogic`, `ProjectileLogic`, `ScreenWrapLogic`, `ApplicationFlowLogic`, `IDamageable`, `IPlayerInput`, `IWaveLogic`, `IFlowLogic`). That's the foundation for DI. The bad news: the SO/event wiring is used as a *poor man's pub-sub* and a *poor man's reactive state container* — both of which DI + C# events replace cleanly.

**Migration pays for itself**: of the 11 SO event/state references in the codebase, **4 have no listeners at all** (`EnemyDestroyed`, `AppPaused`/`AppResumed`, `GameStateStart/Resume/Exit/Pause`, `PlayersLivesValueChanged`). They are 100% dead SOs. Drop them, add a C# event when something actually needs to listen.

---

## 1. What's actually in the repo

**13 packages from `scriptableobject.architecture` family** are pulled from `ThirdPartyPackages/*.tgz`, including `variables`, `variables.database`, `event.variables`, `eventsystem.core/extensions`, `statemachine.core`, `time.machine`, plus VContainer.

**DI footprint today** (everything in `Assets/_Game/Core/VContainer/`):
- `BootLifetimeScope` — 1 `RegisterComponent` for `ApplicationBase` already in scene
- `RootLifetimeScope` — registers `ApplicationFlowLogic` + 2 SOs (`WaveSettings`, `AsteroidSettings`) + a `FiniteStateMachine` + a `GameEvent` keyed

**Concretely SO-bound runtime dependencies** (not in container, not in `[Inject]`):

| Consumer | SO reference | What's it for |
|---|---|---|
| `ApplicationBase` | `AppPaused`, `AppResumed` (GameEvent), `AppPausedTime` (DBInt) | App lifecycle signals |
| `ApplicationFlowController` | `GotoGame`, `GotoLevelFail`, `LevelFailViewClosed` (GameEvent+int), `GameStateTransition`, `LevelFailTransition`, `SettingsTransition` (Transition) | Flow routing |
| `SplashState` | `SceneLoadingProgress` (Float), `HideLoadingView` (GameEvent), `ApplicationFlowControllerReference` (Addressable) | Boot sequence |
| `PlayerController` | `PlayerLives` (Int) | Lives |
| `Asteroid` | `CurrentPlayerScore` (Int), `EnemyDestroyed` (GameEvent) | Score + death signal |
| `LevelFailView` | `LevelFailViewClosedEvent` (GameEvent+int) | Close reason |
| `IntVariableBinder` (HUD) | `DataVariable` (Int), `ValueChangedEvent` (GameEvent) | Score/Lives display |
| `LoadingBar` | `SceneLoadingProgress` (Float) | Progress bar |
| `GameState` / `NormalGameState` | `GameStateStart/Resume/Exit/Pause`, `LevelFail`, `GotoLevelFail` (GameEvent), `CurrentPlayerScore`/`PlayerLives` (Int) | State machine signals + score/lives |
| `RootLifetimeScope` | `TimeMachineTick` (GameEvent) keyed | TimeMachine tick |
| `GameVersionDisplay` | n/a | (clean) |

So there are 11 different GameEvent/Int/Float/DbInt *runtime* dependencies, plus ~6 [Inject] dependencies, in a 63-file codebase. That's the migration surface.

---

## 2. Architecture assessment

### What's right

- **VContainer is already in the manifest** — no infra work needed
- **The two LifetimeScopes are well-structured** — `Boot` (preload via scene reference) + `Root` (global singletons + SOs as instances)
- **WaveSpawner already uses [Inject]** with 4 dependencies (`IWaveLogic`, `WaveSpawnerLogic`, `WaveSettingsSO`, `AsteroidSettingsSO`) — this is the template to copy
- **ApplicationFlowLogic + IFlowLogic is exemplary** — pure POCO strategy table, registered as singleton interface, easily unit-tested (`ApplicationFlowTests` proves it)
- **Pure logic classes everywhere** — `WeaponLogic`, `ProjectileLogic`, `ScreenWrapLogic`, `WaveSpawnerLogic`, `ApplicationFlowLogic` — these are the right shape
- **The constructor for `WaveLogic(WaveSettingsSO settings)` shows you know how to inject SOs** (though that should be a config interface, see §4)
- **`MockPlayerInput` in tests shows you understand the IPlayerInput abstraction** — apply this pattern to the rest

### What's wrong

#### 2.1 The SO-as-pub-sub pattern doesn't scale

Look at how `LevelFailView` closes:

```
LevelFailView (UI)
  → GameEventWithInt.Invoke((int)reason)
  → ApplicationFlowController (subscribed via Handler += ...)
  → IFlowLogic.GetDecision(context, reason)
  → commandMap[intent]()
  → Transition.Transition()
  → StateMachine.Tick()
```

That's **3 indirections to get from a button click to a state change**, and two of them (the `GameEventWithInt` and the `Handler +=` wiring) only exist because the underlying mechanism (C# `event`/interface call) is hidden behind a ScriptableObject. The SO buys you nothing here — you cannot persist, replay, or inspect it. The original Ryan Hipple GameEvent pattern is for *designer decoupling*, not *runtime architecture*. You're at runtime.

**Replace with**: `LevelFailView` has `event Action<UICloseReasons> Closed;` and is constructor-injected into a `LevelFailController` that calls `IFlowLogic.GetDecision(...)` directly. The UI doesn't know about flow at all.

#### 2.2 The SOs are not actually designer-tunable

`PlayerLives`, `CurrentPlayerScore`, `AppPausedTime`, `SceneLoadingProgress` — none of these are authored data. They are **runtime state**. The fact that they live in a `.asset` file in `Core/Data/` and `Core/Variables/` is a smell. The data file is identical to runtime memory, and your `PlayerController` calls `PlayerLives.ResetToDefaultValue()` in `Awake` to "fix" the fact that the SO persists across plays.

This is the "global mutable static" pattern with extra steps.

**Replace with**: Register a `PlayerState` / `GameState` POCO in the container (`Lifetime.Singleton` if single-level, scoped per-match otherwise), expose `event Action<int> LivesChanged` / `ScoreChanged`, and inject it into `PlayerController`, `Asteroid`, `LevelFailViewState`, `IntVariableBinder`, etc.

#### 2.3 Two parallel state machines

You have a `FiniteStateMachine` from `statemachine.core` driving `SplashState → GameState → NormalGameState`, but every consumer *also* has an SO event channel layer for the same transitions (`GameStateStart/Resume/Exit/Pause`, `GotoLevelFail`, `GotoGame`). The SO layer is the "tell" side and the FSM is the "do" side — they are two implementations of the same routing, and they get out of sync (e.g. `NormalGameState.Tick` checks `PlayerLives<=0` *and* listens for `GotoLevelFail`; who wins if both fire?).

**Replace with**: Let the FSM own the routing. After `NormalGameState.Exit()` resolves, call a `IFlowController.GoTo(FlowIntent.GoToLevelFail)` directly. No SO fan-out needed.

#### 2.4 The `[Button]` attribute is the only sign of the SO architecture's design intent

`DebugKillAll()`, `DebugShow()`, `DebugHide()`, `CloseView()` — these are all editor affordances. If your only reason for `GameEvent` is "designers can right-click a SO and raise it in the inspector to test", then a C# `event` with a `[ContextMenu]`-decorated method does the same job without the asset overhead.

#### 2.5 Addressables + Addressables + Addressables

You have **three** Addressables load paths stacked on top of each other:

1. `SplashState.InstantiateApplicationFlowController` loads the FlowController prefab from Addressables
2. `NormalGameState.InstantiateAsteroidSpawnerViaVContainer` loads the WaveSpawner prefab from Addressables
3. `SplashState` then loads `GameScene` via `SceneManager.LoadSceneAsync`
4. `GameState` and `NormalGameState` then load *more* addressable prefabs (HUD, level env, player, spawner)

The reason you needed VContainer's `container.Instantiate(...)` in `SplashState` is to inject into the Addressable-loaded `ApplicationFlowController` (`[Inject]` doesn't run on `Object.Instantiate(prefab)`, so the controller is dead unless VContainer rehydrates it). That's correct usage. But it means the contract is: *"if it's loaded from Addressables, VContainer must re-Instantiate it"*. Make this explicit with a single helper:

```csharp
// In RootLifetimeScope
public T LoadAndInject<T>(AssetReferenceGameObject reference) where T : Component
{
    var op = Addressables.LoadAssetAsync<GameObject>(reference);
    op.WaitForCompletion();          // or coroutine
    return Container.Instantiate(op.Result).GetComponent<T>();
}
```

The same pattern is duplicated in `SplashState.InstantiateApplicationFlowController` and `NormalGameState.InstantiateAsteroidSpawnerViaVContainer` and (partially) `UIViewState.Init`. DRY this.

---

## 3. The split of `Int` / `IntWithEvent` / `DBInt`

You have three flavours of integer SO in `scriptableobject.architecture`:
- `Int` (raw value)
- `IntWithEvent` (with `ValueChanged` GameEvent raised on every set)
- `DBInt` (with database persistence)

**All three are doing the same thing**: a boxed int with change notification. The differences are about *where the notification is observed* (asset reference → subscriber) and *where persistence happens* (DBInt's external `IIntDatabase`).

The cleanest DI replacement:

```csharp
public interface IReadOnlyInt { int Value { get; } event Action<int> Changed; }
public interface IMutableInt : IReadOnlyInt { void Set(int v); void Add(int d); void Reset(); }

public class IntVariable : IMutableInt
{
    private int _value;
    public int Value => _value;
    public event Action<int> Changed;
    public void Set(int v) { if (_value == v) return; _value = v; Changed?.Invoke(v); }
    public void Add(int d)  => Set(_value + d);
    public void Reset()     => Set(_default);
}
```

- **Reads** (`IntVariableBinder` HUD, `PlayerLives <= 0` check) → inject `IReadOnlyInt`
- **Writes** (`PlayerController` `ApplyChange(-amount)`, `Asteroid.Die` score) → inject `IMutableInt`
- **Reset between runs** → either scope the container per match, or call `Reset()` from a setup phase in `NormalGameState.Execute`

For your current game, you need: `PlayerLives`, `CurrentPlayerScore`, `AppPausedTime`, `SceneLoadingProgress` — 4 ints/floats. One `IntVariable`/`FloatVariable` class each, registered as singletons, observers wired by `[Inject]` constructor or `Subscribe()` in `OnEnable`.

---

## 4. SO configs vs DI configs

You have 3 settings SOs today: `PlayerSettingsSO`, `AsteroidSettingsSO`, `WaveSettingsSO`. These are the legitimate use of ScriptableObject — *designer-tunable static data referenced by many consumers*. They should **stay** as SOs (or move to `ScriptableObject` for non-Addressable tuning). But:

- **Don't make them `ScriptableObject` and inject them directly**. Bind to an interface:

  ```csharp
  public interface IPlayerSettings { float ThrustForce { get; } ... }
  public class PlayerSettingsAdapter { public PlayerSettingsAdapter(PlayerSettingsSO so) {...} }
  builder.RegisterInstance(so).AsImplementedInterfaces();
  ```

  This way `PlayerMotor` and `WeaponController` don't take a `ScriptableObject` (Unity-y, untestable without `CreateInstance`), they take `IPlayerSettings` (POCO, fakeable in tests). The `PlayerMovementTests` already do this manually with `ScriptableObject.CreateInstance<PlayerSettingsSO>()` — a code smell that the SO is leaking past the boundary.

- **Register the SOs as `IPlayerSettings`/etc. in `RootLifetimeScope`**. The container's instance registration is the single source of truth for "which settings asset is the active one for this build". No more "drag the right `.asset` into the right field on the right prefab".

- **Drop `AsteroidSettings.TryGetSplitRule` from the asset and put it in a `IAsteroidSplitPolicy`** — split rules change with difficulty, game mode, and designer iteration. They are not data, they are policy. The current `List<SplitRule>` with linear `foreach` search is fine for 3 items but it's the wrong shape — split rule selection should be injected:

  ```csharp
  public interface IAsteroidSplitPolicy { bool TryGetChild(AsteroidSize parent, out AsteroidSize child, out int count); }
  ```

  And your `WaveSpawner` (which already has `[Inject]`) takes `IAsteroidSplitPolicy` next to `WaveSettingsSO`/`AsteroidSettingsSO`. Then `SplitRules` is a list on a `DefaultAsteroidSplitPolicy` POCO with a 3-line ctor.

---

## 5. Event channel pattern (the thing you actually want)

The DI-native equivalent of `GameEvent` is a typed C# event. VContainer has first-class support via `MessagePipe` (optional integration) or you can do it manually:

```csharp
// 1. Declare the message contract
public readonly record struct ScoreChanged(int NewScore);
public readonly record struct PlayerDied;

// 2. Register a publisher and a typed bus
public interface IGameBus
{
    void Publish<T>(T message);
    IDisposable Subscribe<T>(Action<T> handler);
}

// 3. One impl, registered as singleton
public sealed class GameBus : IGameBus { /* ConcurrentDictionary<Type, Delegate> */ }

// 4. Anyone can publish or subscribe
public class Asteroid { private readonly IGameBus _bus; public Asteroid(IGameBus b) => _bus = b; ... _bus.Publish(new PlayerDied()); }
public class PlayerLivesWatcher { public PlayerLivesWatcher(IGameBus bus) => bus.Subscribe<PlayerDied>(OnPlayerDied); }
```

- **Type-safe at compile time** (no `(int)UICloseReasons` casts)
- **No asset references** to wire
- **No nulls in `OnEnable`/`Awake`** (constructor-injected)
- **Unit-testable**: pass a `RecordingBus` into the test, assert it received `ScoreChanged(50)` exactly once
- **Same scope semantics** as DI: a scoped `GameBus` per match resets subscriptions cleanly

**Concrete mappings for your codebase:**

| Today's SO | Today's wiring | New |
|---|---|---|
| `GotoGame` (GameEvent) | `ApplicationFlowController` `Handler += OnGotoGame` | `IFlowController.GoTo(FlowIntent.GoToGame)` method call, or `IGameBus.Publish(new GoToFlowIntent(...))` |
| `LevelFailViewClosed` (GameEvent+int) | `LevelFailView` `Invoke((int)reason)` → `FlowController.OnLevelFailViewClose` | `LevelFailView` fires `event Action<UICloseReasons> Closed` → `LevelFailController` (new) calls `IFlowController.HandleClose(context, reason)` |
| `EnemyDestroyed` (GameEvent) | `Asteroid` `Invoke()` (no listeners in this repo!) | Drop or rename to a typed message |
| `AppPaused`/`AppResumed` (GameEvent) | `ApplicationBase` `Invoke()` (no listeners in this repo!) | Drop or use C# `event` on `ApplicationLifecycle` (new) |
| `GameStateStart/Resume/Pause/Exit` (GameEvent) | `GameState` `Invoke()` (no listeners in this repo!) | Drop — let the FSM call subscribers directly via [Inject] of an `IGameStateObserver` interface |
| `HideLoadingView` (GameEvent) | `SplashState.CompleteBootSequence` `Invoke()` | `LoadingScreen.LoadingBar` listens on `IApplicationLifecycle.GameReady` |
| `PlayerScoreValueChanged` (GameEvent) | `IntVariableBinder` (HUD) `Handler += UpdateUI` | `IntVariable.Changed += UpdateUI`, binder takes `IReadOnlyInt` |
| `PlayersLivesValueChanged` (GameEvent) | (no consumer in this repo) | Wire to HUD via `IntVariable.Changed` |
| `TimeMachineTick` (GameEvent) | Keyed in `RootLifetimeScope` | Inject `ITimeMachine.Tick` (new `ITimeMachine` service) directly |

**Side note**: `EnemyDestroyed`, `AppPaused/Resumed`, `GameStateStart/Resume/Pause/Exit`, `PlayersLivesValueChanged` — none of these have any listeners in the codebase. They are 100% dead SOs. If they were placeholders, fine; but they're 4 of your 11 SO events that the SO architecture demands you keep around "for future flexibility" that never materializes. Drop them and add a C# event when something actually needs to listen.

---

## 6. Concrete file-by-file recommendations

### 6.1 `Assets/_Game/Core/VContainer/RootLifetimeScope.cs`

Currently registers 4 things, of which 2 are SOs. Add the missing singletons:

```csharp
protected override void Configure(IContainerBuilder builder)
{
    // === Services ===
    builder.Register<ApplicationFlowLogic>(Lifetime.Singleton).AsImplementedInterfaces();
    builder.Register<IGameBus, GameBus>(Lifetime.Singleton);

    // === Time ===
    builder.RegisterInstance(ApplicationStateMachine).AsSelf();
    builder.Register<ITimeMachine, ApplicationTimeMachineAdapter>(Lifetime.Singleton);

    // === Game state (mutable) ===
    builder.Register<IntVariable>(Lifetime.Singleton).As<IReadOnlyInt, IMutableInt>();   // PlayerLives
    builder.Register<IntVariable>(Lifetime.Singleton).As<IReadOnlyInt, IMutableInt>();   // PlayerScore
    // Use keyed registrations if same type is awkward:
    //   .WithKey("Lives").WithKey("Score") then inject Keyed<IMutableInt>(key)

    // === Configs (data, not logic) ===
    builder.RegisterInstance(WaveSettings).AsImplementedInterfaces();
    builder.RegisterInstance(AsteroidSettings).AsImplementedInterfaces();

    // === Flow events ===
    // Delete the GameEvent reference entirely; IFlowController replaces it
}
```

### 6.2 `ApplicationBase.cs`

Has 3 SO dependencies and 1 [Inject]. After migration:

```csharp
public class ApplicationBase : IAsyncStartable
{
    private readonly IntVariable _appPausedTime;
    private readonly FiniteStateMachine _stateMachine;
    private readonly ITimeMachine _timeMachine;
    private readonly IGameBus _bus;

    public ApplicationBase(FiniteStateMachine sm, ITimeMachine tm,
        [Key("AppPausedTime")] IntVariable appPausedTime, IGameBus bus) {...}

    public async Task StartAsync()  // VContainer's IAsyncStartable
    {
        _timeMachine.Start();
        _stateMachine.Start();
    }
}
```

Then `ApplicationPaused()` becomes `_bus.Publish(new AppLifecycleChanged(PausedState))` (or just an `event` on this class). Drop the 3 `[SerializeField]` SOs.

### 6.3 `ApplicationFlowController.cs`

Has 6 SO fields, 3 [Inject]. After:

```csharp
public class ApplicationFlowController : IStartable, IDisposable
{
    private readonly IFlowLogic _logic;
    private readonly FiniteStateMachine _stateMachine;
    private readonly Dictionary<FlowIntent, ITransition> _commandMap;
    private readonly IDisposable _subscription;

    public ApplicationFlowController(IFlowLogic logic, FiniteStateMachine sm, IGameBus bus)
    {
        _logic = logic; _stateMachine = sm;
        _subscription = bus.Subscribe<FlowIntent>(ExecuteIntent);
    }

    public void Start() => Boot();

    public void Dispose() => _subscription?.Dispose();
}
```

Three `GameEvent` SOs (`GotoGame`, `GotoLevelFail`, `LevelFailViewClosed`) → one bus subscription on `FlowIntent`. The 3 `Transition` SOs become a `Dictionary<FlowIntent, ITransition>` populated in the constructor:

```csharp
// 6.4 New file: Assets/_Game/Core/Scripts/FlowTransitions.cs
public record FlowTransitions(
    ITransition Game,
    ITransition LevelFail,
    ITransition Settings);
```

The `UIViewTransition` ScriptableObject can stay *only* if you need them to be Addressable. Otherwise it's just an `ITransition` POCO with a `ToState` reference, registered in the container.

### 6.5 `SplashState.cs`

Still uses `SceneLoadingProgress` (Float) and `HideLoadingView` (GameEvent) and addressably-loads `ApplicationFlowController`. After:

- Inject `IReadOnlyFloat SceneLoadingProgress` (your HUD reads this) and `IGameBus bus`
- Use `bus.Publish(new SceneLoadProgress(0.0f))` instead of `SetValue(0)`
- Use `bus.Publish(new HideLoadingScreen())` instead of `Invoke()`
- Replace the `LifetimeScope.Find<RootLifetimeScope>()` with a constructor-injected `RootLifetimeScope` (VContainer resolves it automatically) — that "the bridge between SO and VContainer" comment is a smell

The 2 [Inject]-less [SerializeField] SOs go away.

### 6.6 `PlayerController.cs`

Currently has a `[SerializeField] Int PlayerLives` and `Initialize(InputReader, Settings)`. After:

```csharp
public class PlayerController : MonoBehaviour, IDamageable
{
    private readonly IMutableInt _lives;
    private readonly PlayerMotor _motor;
    private readonly WeaponController _weapon;
    private readonly PlayerSettings _settings;

    [Inject]
    public PlayerController(
        [Key("PlayerLives")] IMutableInt lives,
        PlayerMotor motor, WeaponController weapon, PlayerSettings settings)
    {
        _lives = lives; _motor = motor; _weapon = weapon; _settings = settings;
    }
}
```

Make `PlayerMotor` and `WeaponController` MonoBehaviours resolved through the scene's child LifetimeScope (VContainer has `RegisterComponentInHierarchy` for exactly this) so you don't have to write `Initialize(...)` glue.

### 6.7 `WeaponController.cs`

The 9-line block of commented-out code (`//[SerializeField] private ProjectilePool ProjectilePool;`) is a leftover from the half-migration. Either rewire the pool as a [Inject] dependency or delete the comments. After:

```csharp
public class WeaponController : MonoBehaviour
{
    private readonly IPlayerInput _input;
    private readonly IPool<Projectile> _pool;
    private readonly WeaponLogic _logic;
    private readonly PlayerSettings _settings;

    [Inject]
    public WeaponController(IPlayerInput input, IPool<Projectile> pool, PlayerSettings settings)
    {
        _input = input; _pool = pool; _settings = settings;
        _logic = new WeaponLogic();
    }
}
```

The pool is already DI-friendly (`IPool<T>` exists); register `ProjectilePool` once in the gameplay scope.

### 6.8 `Asteroid.cs`

Currently reads `CurrentPlayerScore` (Int) and `EnemyDestroyed` (GameEvent) via [SerializeField]. Replace with `IMutableInt Score` + `IGameBus`. Note also: `EnemyDestroyed` has no consumers in this repo. If it's truly dead, just drop the field. If you actually want score-death coupling, make the HUD bind to the bus's `ScoreChanged` event.

The `Action<AsteroidSize, Vector3> _splitAction` callback pattern is good — keep it. DI does not improve it.

### 6.9 `LevelFailView.cs`

Has 1 SO field (`LevelFailViewClosedEvent`). Replace with a C# event on the view itself:

```csharp
public class LevelFailView : UIPanelInAndOut
{
    public event Action<UICloseReasons> Closed;
    private void OnRestartClicked() => Closed?.Invoke(UICloseReasons.Game);
}
```

A new `LevelFailController` (in `Assets/_Game/UI/LevelFail/`) takes the view in its constructor and routes the close through `IFlowController`. The view no longer knows about flow.

### 6.10 `IntVariableBinder.cs` (HUD)

Takes 2 SOs (`Int`, `GameEvent`). After: inject `IReadOnlyInt` only. Subscribe to `.Changed` in `OnEnable`, unsubscribe in `OnDisable`. No more `DataVariable == null` checks.

### 6.11 `LoadingBar.cs`

Same pattern as `IntVariableBinder`. Inject `IReadOnlyFloat`.

### 6.12 `NormalGameState.cs`

The two SOs `CurrentPlayerScore` and `PlayerLives` (line 29-30) — drop. Get them via the bus or via constructor injection. The `[SerializeField] Int CurrentPlayerScore.SetValue(0)` call becomes `_score.Reset()`. The `PlayerLives <= 0` check on line 74 becomes a bus subscription (you want the *event* of reaching 0, not a poll every tick — that's a real bug-fix the migration brings for free).

### 6.13 `GameState.cs` / `UIViewState.cs`

`GameState` has 5 GameEvent SOs (`GameStateStart`, `GameStateResume`, `GameStateExit`, `GameStatePause`, `LevelFail`) and *no listeners in this repo*. Delete them; FSM state changes are already observable via the state machine's own callbacks. If the HUD wants to know "the game state started", inject `IGameState` and read its `event Action Started`.

`UIViewState` has `GameEventWithInt EventWithCloseReason` — this is the **one** legitimate current use of the GameEvent pattern (a UI view needs to send a reason to the flow controller without knowing it exists). But the SO is still the wrong shape: make it a method call.

### 6.14 The .asset files

After migration, the only `.asset` files you should have are:
- `PlayerSettings.asset` (designer-tunable)
- `AsteroidSettings.asset` (designer-tunable) — minus split rules (move to a POCO `IAsteroidSplitPolicy`)
- `WaveSettings.asset` (designer-tunable)
- `UIConfig_Default.asset` (designer-tunable)
- `GameHudConfig.asset` (designer-tunable)
- `StateMachine.asset` (the FSM, a `ScriptableObject` from the package — keep, this is legit)
- `SplashState.asset`, `NormalGameState.asset`, `LevelFailViewState.asset` (states — keep if your FSM uses SOs, otherwise make them POCOs registered in the container)
- `*Transition.asset` (keep if Addressable; otherwise POCOs)

Delete:
- `v_CurrentPlayerScore.asset`, `v_PlayerLives.asset`, `v_AppPausedTime.asset`, `v_SceneLoadingProgress.asset`, `v_SdkLoadingProgress.asset` (runtime state)
- `e_AppPaused.asset`, `e_AppResumed.asset`, `e_PlayerScoreValueChanged.asset`, `e_PlayersLivesValueChanged.asset`, `e_EnemyDestroyed.asset`, `e_GameStateExit.asset`, `e_GameStatePause.asset`, `e_GameStateResume.asset`, `e_GameStateStart.asset`, `e_GotoGame.asset`, `e_GotoLevelFail.asset`, `e_LevelFailViewClose.asset`, `e_HideLoadingView.asset` (all events)
- SettingsViewTransition (unused in the codebase)

That's 18 .asset files (60% of the 30 currently in the project) deleted, replaced with 4 POCO services and 1 event bus.

---

## 7. Migration plan (concrete, ordered)

I would do this in 4 phases to keep the project compiling after each step.

### Phase 1 — Add the bus + variable types

1. Add `IntVariable`, `FloatVariable`, `IGameBus`, `GameBus` POCOs in `Assets/_Game/Core/Variables/` and `Assets/_Game/Core/Events/`.
2. Register them in `RootLifetimeScope`.
3. Add `[Inject]` to `WaveSpawner`'s remaining SO dependencies (already done) — confirm the project still runs.
4. Run all tests. If green, this phase is safe.

### Phase 2 — Migrate runtime state (Int/Float)

1. Replace `Int PlayerLives` on `PlayerController` with `[Inject] IMutableInt`.
2. Replace `Int CurrentPlayerScore` + `Int PlayerLives` on `NormalGameState` with `[Inject] IMutableInt` × 2.
3. Replace `Int CurrentPlayerScore` + `GameEvent EnemyDestroyed` on `Asteroid` with `[Inject] IMutableInt` (drop the GameEvent — no consumers).
4. Replace `Float SceneLoadingProgress` on `SplashState` with `[Inject] IReadOnlyFloat` for writing + bus for the "I changed" signal.
5. Replace `Int` + `GameEvent` on `IntVariableBinder` with `[Inject] IReadOnlyInt` + `.Changed` event.
6. Replace `Float SceneLoadingProgress` on `LoadingBar` with `[Inject] IReadOnlyFloat`.
7. Delete the 5 `v_*.asset` files, the 2 `e_Player*ValueChanged.asset` files, the `e_EnemyDestroyed.asset` file. Update any remaining prefab/scene references (now invalid).

### Phase 3 — Migrate events (GameEvent → bus)

1. Add `FlowIntent` + `UICloseReason` as a C# `event` on a new `IFlowController` service.
2. Replace `GotoGame`/`GotoLevelFail`/`LevelFailViewClosed` subscriptions in `ApplicationFlowController` with the bus.
3. Replace `AppPaused`/`AppResumed` in `ApplicationBase` with a `event` on `ApplicationBase` itself.
4. Delete the 9 `e_*.asset` files in `Core/Events/`, `GamePlay/Events/`, `UI/LevelFail/Events/`, `UI/LoadingScreen/Events/`. Also drop these dependencies from `Packages/manifest.json` once nothing in your code references them: `com.madratzz.scriptableobject.event.variables`, `com.madratzz.scriptableobject.eventsystem.core`, `com.madratzz.scriptableobject.eventsystem.extensions`. Keep the `scriptableobject.architecture` package (or drop it once states are POCOs too).

### Phase 4 — Migrate settings SOs to interface

1. Add `IPlayerSettings`, `IAsteroidSettings`, `IWaveSettings` interfaces.
2. Make the *SOs* implement them and register the interface in `RootLifetimeScope`.
3. Update `PlayerMotor` / `WeaponController` to take interfaces.
4. Update `WaveSpawner` / `Asteroid` accordingly.
5. Drop `AsteroidSettings.SplitRules` into a `DefaultAsteroidSplitPolicy` POCO registered in the container.
6. Delete the scriptableobject.architecture / variables packages from the manifest.

After Phase 4, your `Packages/manifest.json` loses ~7 third-party packages and your `Assets/_Game/Core/VContainer/` has 4 services registered; everything else is `MonoBehaviour` + `class` with `[Inject]`.

---

## 8. Smaller things worth fixing now (not migration-related)

1. **`PlayerMovementTests.Player_Should_Stop_Rotating_When_Input_Zero` (line 119-134)** — the assertion is `Assert.AreEqual(rotationWhileMoving, _rb.rotation, 0.1f)`. This is correct but the implementation drift is that you set `_mockInput.RotationInput = 0f` and wait 2 `WaitForFixedUpdate` calls. With `DragFactor = 0` and the test setup using `RotationSpeed = 90f`, the test will only pass if you wait until rotation has actually stopped, which depends on physics. Consider asserting the *delta* over time:

   ```csharp
   float before = _rb.rotation;
   yield return new WaitForSeconds(0.1f);
   Assert.That(_rb.rotation - before, Is.LessThan(0.5f));
   ```

2. **`WaveSpawner._cam = Camera.main` in `Awake`** (line 53) — `Camera.main` is the #1 "doesn't work in additive scene loads" Unity bug. Use `Camera.main.GetComponent<Camera>()` cached after the gameplay scene loads, or pass it in via the GameplayScope.

3. **`WaveSpawner.DebugKillAll` (line 154-171)** uses `FindObjectsByType<Asteroid>` — this hits every asteroid in every scene, including ones that haven't been returned to the pool. Use `Object.FindObjectsByType<Asteroid>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)` and skip pooled instances. (Or expose `IPool<>.Clear()` and call that.)

4. **`ApplicationBase.Construct` is a method-injection, not a constructor injection** (line 26-30) — fine for `MonoBehaviour`, but you're inconsistent: `WaveSpawner` and `ApplicationFlowController` also use `[Inject] public void Construct(...)`. Pick one — VContainer's `RegisterComponent` will call either. Constructor injection is preferable for `IStartable` / `IAsyncStartable` services because it's clear what the dependencies are.

5. **`ApplicationFlowController.OnEnable`/`OnDestroy` subscribes to GameEvents** but the events can be null. The constructor-injection + bus pattern makes this entire `if (GotoGame)` block disappear.

6. **`BootLifetimeScope.RegisterComponent(ApplicationBase)`** is a one-liner that does nothing the scene wouldn't do already (`[SerializeField] ApplicationBase` is set in the inspector). This scope is dead weight. Either delete it or use it to register the actual singleton services.

7. **`RootLifetimeScope` uses `.Keyed("TimeMachineTick")`** — nobody in the repo consumes this. If it stays, write a comment explaining which class uses the key. If it doesn't, delete the registration and the SO.

8. **`SplashState.InstantiateApplicationFlowController` (line 67) calls `container.Instantiate(handle.Result.GetComponent<ApplicationFlowController>())`** — this is the right pattern, but it's duplicated in `NormalGameState.InstantiateAsteroidSpawnerViaVContainer` (line 141) and partially in `UIViewState.Init`. Extract a helper (see §2.5).

9. **`Asteroid.Die` and `Asteroid.OnTriggerEnter2D`** (lines 61-68, 77-85) both check `if (CurrentPlayerScore != null)` and `if (EnemyDestroyed != null)`. Once these are [Inject]-d, the null checks go away — `if (_score != null) _score.ApplyChange(...)` becomes just `_score.Add(...)`. Same for `IntVariableBinder.OnEnable` (line 27), `IntVariableBinder.OnDisable` (line 35), `PlayerController.RespawnRoutine` (line 53).

10. **`PlayerController._isRespawning` flag** is set true, `yield return new WaitForSeconds(RespawnDelay)`, then set false — but if `TakeDamage` is called from `Asteroid.OnTriggerEnter2D` while the coroutine is waiting, `_isRespawning` guards re-entry. Fine. But the same damage is also applied in the asteroid-player collision (line 83) *and* from `Projectile.OnTriggerEnter2D` (line 54). The "if the player just died in a chain, don't apply damage twice" guard is implicit. Consider an explicit `IDamageable.CanTakeDamage` or accept a DamageInfo struct so you can deduplicate.

11. **`[Button]` attribute** (`WaveSpawner.DebugKillAll`, `UIViewState.CloseView`, `UIView.DebugShow/DebugHide`, `GameHudBar.Show/Hide`) — these are great for editor iteration but they're public on runtime classes. Move them to an `Editor/` folder so they don't ship.

12. **State `SplashState` and `NormalGameState` are `ScriptableObject` subclasses of `State`** — they have a hidden dependency on the FSM framework. You can't `new` them in tests, can't easily subclass, can't easily mock. Consider making states POCOs and the FSM framework's `State` an interface (`IState` it already is — your states just don't implement it directly, they inherit from the framework's `State`). This is the same anti-pattern as the SOs: "global mutable" dressed as a Unity asset.

---

## 9. TL;DR Migration List

| Item | Today | Target |
|---|---|---|
| `scriptableobject.event.variables` | 1 dep in 3 classes | Drop, use `IGameBus` |
| `scriptableobject.eventsystem.core` | 1 dep in 1 class | Drop, use `IGameBus` |
| `scriptableobject.eventsystem.extensions` | 0 in code (only transitive) | Drop |
| `scriptableobject.variables` | 7 references in 6 classes | Drop, use `IntVariable` POCO |
| `scriptableobject.variables.database` | 1 reference (`DBInt`) | Drop |
| `scriptableobject.architecture` | 1 state SO + 2 view state SOs | Drop if you migrate states to POCOs |
| `scriptableobject.statemachine.core` | 1 state machine + 4 state SOs | Keep, this is the legit "framework" use of SOs |
| `scriptableobject.time.machine` | 1 `TimeMachine.Tick()` coroutine | Inject as `ITimeMachine` |
| Settings SOs (Player/Asteroid/Wave) | Direct injection of concrete class | Inject as `I*Settings` interface |
| Runtime state SOs (PlayerLives/Score/etc) | `Int`/`Float`/`DBInt` | `IntVariable`/`FloatVariable` POCO |
| Event SOs (Goto*/Pause/Resume/etc) | `GameEvent`/`GameEventWithInt` | `IGameBus` typed messages |
| Addressable-instantiate-and-[Inject] pattern | 3 sites copy-pasted | Single `LoadAndInject<T>` helper in `RootLifetimeScope` |
| `[SerializeField] private T Component` references in MonoBehaviours | ~25 fields across 12 classes | `[Inject]` (constructor for `IStartable`, `IAsyncStartable`; method-injection for `MonoBehaviour`) |
| `.asset` count | 30 | ~12 (settings + FSM + 4 transition POCOs as assets) |

You don't need a rewrite — you need to delete things. ~70% of the SO event/variable inventory is dead weight, and the rest is just pub-sub and reactive state, both of which are 1-day implementations in C#.

---

## 10. Suggested next step

Start with **Phase 1** (the bus + variable types + the `WaveSpawner` consolidation) so you can see one working end-to-end migration before committing to the rest. This gives you:

- `IGameBus`, `GameBus`, `IntVariable`, `FloatVariable` POCOs
- `RootLifetimeScope` registering them
- The first consumer rewired (e.g. `IntVariableBinder` reading the bus / variable) to prove the pattern
- A green test suite confirming nothing regressed

After Phase 1, the rest of the migration is mechanical.
