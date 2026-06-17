using ProjectGame.Core.Pooling;
using UnityEngine.AddressableAssets;

namespace ProjectGame.Features.Enemies
{
    // Inherits all the Addressables and Container logic!
    public class AsteroidPool : ObjectPoolBase<Asteroid>
    {
        public AsteroidPool(AssetReference assetReference, int defaultCapacity = 10, int maxSize = 50) : base(assetReference, defaultCapacity, maxSize)
        {
        }
    }
}