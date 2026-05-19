# Assembly Definition Migration Guide

Moving from the single `Project.Runtime.asmdef` to per-feature assemblies.
Complete one feature at a time, in the order listed below — each step should
compile cleanly before moving to the next.

## Dependency Graph (Target State)

```
Project.Core.Interfaces        ← nothing
        ↑
Project.Core.Runtime           ← Core.Interfaces, VContainer, SO packages
        ↑                ↑
Project.Feature.ScreenWrap     Project.Feature.InputReader
        ↑                ↑              ↑
Project.Feature.Asteroids      Project.Feature.Weapons
        ↑                              ↑
Project.Feature.Waves          Project.Feature.Player ← InputReader, Weapons, ScreenWrap
        ↑                              ↑
        └──────────── Project.GamePlay ┘
                              ↑
                       Project.UI
```

## Migration Steps

### Step 0 — Preparation (do first)
1. Set `"autoReferenced": false` on `Project.Runtime.asmdef`.
   Every assembly that currently relied on auto-referencing will now need
   explicit references — compile errors will appear and guide you.
2. Commit the compile errors list as a baseline.

### Step 1 — `Project.Core.Interfaces`
**Target folder:** `Assets/_Game/Core/Scripts/Interfaces/`
**Scripts moved:** `IDamageable.cs`, `IFlowLogic.cs`
**Also move:** `Assets/_Game/Core/Pooling/Interfaces/IPool.cs`
**References:** _(none)_

Verify: `Project.Runtime` adds a reference to `Project.Core.Interfaces`.

---

### Step 2 — `Project.Feature.ScreenWrap`
**Target folder:** `Assets/_Game/Features/ScreenWrap/Scripts/`
**Scripts:** `ScreenWrap.cs`, `ScreenWrapLogic.cs`, `ScreenWrapDebugger.cs`
**References:** `Project.Core.Interfaces`

---

### Step 3 — `Project.Feature.InputReader`
**Target folder:** `Assets/_Game/Features/InputReader/Scripts/`
**Scripts:** `InputSystemReader.cs`, `GameControls.cs`
**References:** `Project.Core.Interfaces`, `Unity.InputSystem`

Note: `IPlayerInput` lives in `Features/Player/Scripts/Interfaces/`. InputReader
must reference Feature.Player (or a shared contracts assembly) to implement it.
Simplest resolution: move `IPlayerInput` to `Project.Core.Interfaces`.

---

### Step 4 — `Project.Feature.Weapons`
**Target folder:** `Assets/_Game/Features/Weapons/Scripts/`
**Scripts:** `WeaponController.cs`, `WeaponLogic.cs`, `Projectile.cs`,
            `ProjectileLogic.cs`, `ProjectilePool.cs`
**References:** `Project.Core.Runtime`, `Project.Core.Interfaces`,
               `Project.Feature.InputReader`

---

### Step 5 — `Project.Feature.Asteroids`
**Target folder:** `Assets/_Game/Features/Asteroids/Scripts/`
**Scripts:** `Asteroid.cs`, `AsteroidPool.cs`, `AsteroidSettingsSO.cs`, `AsteroidSize.cs`
**References:** `Project.Core.Runtime`, `Project.Core.Interfaces`,
               `Project.Feature.ScreenWrap`

Action required: Remove `[SerializeField] private Int CurrentPlayerScore` from
`Asteroid.cs` — the Asteroid feature should not reach into Core variables.
Fire a `ScoreChanged` event instead and let Core subscribe.

---

### Step 6 — `Project.Feature.Waves`
**Target folder:** `Assets/_Game/Features/Waves/Scripts/`
**Scripts:** `WaveLogic.cs`, `WaveSettingsSO.cs`, `WaveSpawner.cs`, `WaveSpawnerLogic.cs`
**References:** `Project.Core.Runtime`, `Project.Feature.Asteroids`

---

### Step 7 — `Project.Feature.Player`
**Target folder:** `Assets/_Game/Features/Player/Scripts/`
**Scripts:** `PlayerController.cs`, `PlayerMotor.cs`, `PlayerSettingsSO.cs`,
            `Interfaces/IPlayerInput.cs`
**References:** `Project.Core.Runtime`, `Project.Core.Interfaces`,
               `Project.Feature.InputReader`, `Project.Feature.Weapons`,
               `Project.Feature.ScreenWrap`

---

### Step 8 — `Project.GamePlay`
**Target folder:** `Assets/_Game/GamePlay/Scripts/`
**Scripts:** `GameState.cs`, `NormalGameState.cs`
**References:** `Project.Core.Runtime`, `Project.Feature.Player`,
               `Project.Feature.Asteroids`, `Project.Feature.Waves`

Action required: Fix `RootLifetimeScope` — move `WaveSettingsSO` and
`AsteroidSettingsSO` registrations to a new `GameLifetimeScope` in this assembly.

---

### Step 9 — `Project.UI`
**Target folder:** `Assets/_Game/UI/`
**Scripts:** All UI scripts
**References:** `Project.Core.Runtime`, `Project.Core.Interfaces`

---

### Step 10 — `Project.Core.Runtime`
Remove `Project.Runtime.asmdef` once all features are migrated.
Set each test assembly to reference the specific feature assemblies it tests.

## Rules to Enforce Going Forward

1. `autoReferenced: false` on all feature assemblies.
2. No feature assembly may reference another feature assembly except through
   `Project.Core.Interfaces` types — cross-feature communication via events only.
3. `Project.Core.Runtime` may not reference any `Project.Feature.*` assembly.
4. Every new assembly must have at least one unit test assembly referencing it.
