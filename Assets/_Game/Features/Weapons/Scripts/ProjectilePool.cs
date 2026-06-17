using ProjectGame.Core.Pooling;
using UnityEngine.AddressableAssets;

namespace ProjectGame.Features.Weapons
{
    public class ProjectilePool : ObjectPoolBase<Projectile>
    {
        public ProjectilePool(AssetReference assetReference, int defaultCapacity = 10, int maxSize = 50) : base(assetReference, defaultCapacity, maxSize)
        {
        }
    }
}