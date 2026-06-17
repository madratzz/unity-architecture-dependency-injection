using System.Collections.Generic;
using CustomUtilities.Attributes;
using ProjectGame.Core.Pooling;
using ProjectGame.Features.Enemies;
using UnityEngine;
using ProjectGame.Features.Enemies.Logic;
using ProjectGame.Features.Waves.Logic;
using UnityEngine.AddressableAssets;
using VContainer;

namespace ProjectGame.Features.Waves
{
    public class WaveSpawner : MonoBehaviour
    {
        [Header("Pool Configuration")]
        [SerializeField] private List<PoolBinding> PoolRegistry;
        
        private IWaveLogic _waveLogic;
        private WaveSpawnerLogic _spawnLogic;
        private WaveSettingsSO _waveSettings;
        private AsteroidSettingsSO _asteroidSettings;
        
        private Dictionary<AsteroidSize, IPool<Asteroid>> _poolMap;
        
        private int _activeAsteroidCount = 0;
        private float _currentWaveSpeed;
        private bool _isSpawning;
        private Camera _cam;
        private float _camHeight;
        private float _camWidth;
        
        private bool _isInitializationComplete = false;

        [Inject]
        public void Construct(
            IWaveLogic waveLogic, 
            WaveSpawnerLogic spawnLogic, 
            WaveSettingsSO waveSettings,
            AsteroidSettingsSO asteroidSettings)
        {
            _waveLogic = waveLogic;
            _spawnLogic = spawnLogic;
            _waveSettings = waveSettings;
            _asteroidSettings = asteroidSettings;
        }
        
        private void OnEnable()
        {
            _activeAsteroidCount = 0;
        }
        
        private void Awake()
        {
            _cam = Camera.main;
            CalculateScreenBounds();
            InitializePools();
        }
        
        private void InitializePools()
        {
            _poolMap = new Dictionary<AsteroidSize, IPool<Asteroid>>();

            foreach (var binding in PoolRegistry)
            {
                if (binding.AsteroidAssetReference != null)
                {
                    // This handles ANY size you add to the Enum later!
                    _poolMap[binding.Size] = new AsteroidPool(binding.AsteroidAssetReference);//binding.Pool;
                }
            }
        }
        

        private void CalculateScreenBounds()
        {
            if (_cam == null) return;
            _camHeight = _cam.orthographicSize;
            _camWidth = _camHeight * _cam.aspect;
        }

        private void Update()
        {
            if (!_isInitializationComplete)
            {
                if (!AreAllPoolsReady()) return; // Still loading... try again next frame
        
                _isInitializationComplete = true; // DONE! Stop asking.
                Debug.Log("[AsteroidSpawner] All pools ready. Starting Game Loop.");
            }
            
            if (_isSpawning || _activeAsteroidCount > 0) return;
            Debug.Log($"Wave {_waveLogic.CurrentWave} Complete. Starting Next...");
            StartNextWave();
        }
        
        private void StartNextWave()
        {
            _isSpawning = true;
            
            int wave = _waveLogic.NextWave();
            int count = _waveLogic.CalculateAsteroidCount(wave);
            
            _currentWaveSpeed = _waveLogic.CalculateWaveSpeed(wave, _asteroidSettings.BaseSpeed);

            for (int i = 0; i < count; i++)
            {
                SpawnAsteroid(AsteroidSize.Large, GetRandomEdgePosition());
            }

            _isSpawning = false;
        }

        private void SpawnAsteroid(AsteroidSize size, Vector2 position)
        {
            if (!_poolMap.TryGetValue(size, out IPool<Asteroid> pool) || !pool.IsReady) return;
            
            Asteroid asteroid = pool.Get();
            asteroid.Initialize(position, size, _asteroidSettings, _currentWaveSpeed, pool.Release, HandleSplit);

            _activeAsteroidCount++;
        }
        
        private void HandleSplit(AsteroidSize size, Vector3 position)
        {
            _activeAsteroidCount--; // Parent died

            if (_asteroidSettings.TryGetSplitRule(size, out AsteroidSize childSize, out int count))
            {
                for (int i = 0; i < count; i++)
                {
                    SpawnAsteroid(childSize, position);
                }
            }
        }
        
        private Vector2 GetRandomEdgePosition()
        {
            return _spawnLogic.GetRandomSpawnPosition(_camHeight, _camWidth, _waveSettings.SpawnBuffer);
        }
        
        private bool AreAllPoolsReady()
        {
            if (_poolMap == null || _poolMap.Count == 0) return false;

            // It is okay to loop here because we ONLY do it for the first few seconds.
            foreach (var pool in _poolMap.Values)
            {
                if (!pool.IsReady) return false;
            }
            return true;
        }
        
        [Button]
        public void DebugKillAll()
        {
            // 1. Find all ACTIVE asteroids in the scene
            // (FindObjectsByType only finds active objects by default)
            var allAsteroids = FindObjectsByType<Asteroid>(FindObjectsSortMode.None);

            foreach (var asteroid in allAsteroids)
            {
                // 2. Send them back to the pool immediately.
                // We call Release() instead of Die() to avoid spawning split pieces or adding score.
                asteroid.Release();
            }

            // 3. Force the logic to recognize the wave is cleared
            _activeAsteroidCount = 0;
    
            Debug.Log($"[DEBUG] Removed {allAsteroids.Length} asteroids. Next wave starting...");
        }
        
        [System.Serializable]
        public struct PoolBinding
        {
            public AsteroidSize Size;
            public AssetReference AsteroidAssetReference;
        }
    }
}