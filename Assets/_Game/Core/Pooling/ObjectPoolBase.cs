using System;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace ProjectGame.Core.Pooling
{
    public abstract class ObjectPoolBase<T> : IPool<T> where T : Component
    {
        private readonly AssetReference _prefabReference;
        private readonly int _defaultCapacity;
        private readonly int _maxSize;

        private ObjectPool<T> _pool;
        private GameObject _loadedPrefab;
        private AsyncOperationHandle<GameObject> _prefabHandle;
        private Transform _container;
        
        public bool IsReady { get; private set; }
        public event Action OnInitialized;
        

        public ObjectPoolBase(AssetReference assetReference, int defaultCapacity=10, int maxSize=50)
        {
            _prefabReference = assetReference;
            _defaultCapacity = defaultCapacity;
            _maxSize = maxSize;
            
            LoadAsset();
        }

        private void LoadAsset()
        {
            _prefabReference.LoadAssetAsync<GameObject>().Completed += handle =>
            {
                if (handle.Status == AsyncOperationStatus.Succeeded)
                {
                    _loadedPrefab = handle.Result;
                    _prefabHandle = handle;
                    CreateContainer();
                    InitializePool();
                    
                    IsReady = true;
                    OnInitialized?.Invoke();
                }
                else
                {
                    Debug.LogError($"Pool failed to load: {handle.OperationException}");
                }
            };
        }

        private void CreateContainer()
        {
            var obj = new GameObject($"--- Pool: {_prefabReference.Asset.name} ---");
            _container = obj.transform;
        }

        private void InitializePool()
        {
            _pool = new ObjectPool<T>(
                createFunc: CreateItem,
                actionOnGet: OnGetItem,
                actionOnRelease: OnReleaseItem,
                actionOnDestroy: OnDestroyItem,
                collectionCheck: false,
                defaultCapacity: _defaultCapacity,
                maxSize: _maxSize
            );
        }
        
        protected virtual T CreateItem()
        {
            return UnityEngine.Object.Instantiate(_loadedPrefab, _container).GetComponent<T>();
        }

        protected virtual void OnGetItem(T item) => item.gameObject.SetActive(true);
        protected virtual void OnReleaseItem(T item) => item.gameObject.SetActive(false);
        protected virtual void OnDestroyItem(T item) => UnityEngine.Object.Destroy(item.gameObject);
        
        
        public T Get() => _pool.Get();
        public void Release(T item) => _pool.Release(item);

        protected virtual void OnDestroy()
        {
            if (_container != null) UnityEngine.Object.Destroy(_container.gameObject);
            if (_prefabHandle.IsValid()) Addressables.Release(_prefabHandle);
        }
    }
}