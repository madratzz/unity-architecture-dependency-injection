using ProjectGame.Core.Pooling;
using UnityEngine;
using ProjectGame.Features.Player.Configs;
using ProjectGame.Features.Player.Interfaces;
using ProjectGame.Features.Weapons.Logic;

namespace ProjectGame.Features.Weapons
{
    public class WeaponController : MonoBehaviour
    {
        [Header("Dependencies")]
        //[SerializeField] private ProjectilePool ProjectilePool;
        [SerializeField] private Transform FirePoint;

        private IPool<Projectile> _pool;
        
        private IPlayerInput _input;
        private PlayerSettingsSO _settings;
        private readonly WeaponLogic _weaponLogic = new WeaponLogic();

        public void Initialize(IPlayerInput input, PlayerSettingsSO settings)
        {
            _input = input;
            _settings = settings;
            _pool = new ProjectilePool(settings.BulletPrefabReference);
            //if (ProjectilePool != null) _pool = ProjectilePool;
        }
        
        // private void Awake()
        // {
        //     if (_pool == null && ProjectilePool != null)
        //     {
        //         _pool = ProjectilePool;
        //     }
        // }

        private void Update()
        {
            if (_input == null || _pool ==null || !_pool.IsReady) return;

            if (_weaponLogic.ShouldFire(_input.IsFiring, Time.time, _settings.FireRate))
            {
                Fire();
            }
        }

        private void Fire()
        {
            Projectile bullet = _pool.Get();
            
            bullet.transform.position = FirePoint.position;
            bullet.transform.rotation = FirePoint.rotation;

            // Launch
            bullet.Initialize(
                direction: transform.up, 
                speed: _settings.BulletSpeed, 
                lifetime: _settings.BulletLifetime,
                damage: _settings.Damage,
                returnAction: _pool.Release 
            );
        }
    }
}