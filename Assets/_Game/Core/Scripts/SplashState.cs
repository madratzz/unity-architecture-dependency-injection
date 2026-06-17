using System;
using System.Collections;
using ProjectCore.Events;
using ProjectCore.StateMachine;
using ProjectCore.Utilities;
using ProjectCore.Variables;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;
using VContainer.Unity;

namespace ProjectCore
{
    [CreateAssetMenu(fileName = "SplashState", menuName = "ProjectCore/State Machine/States/Splash State")]
    public class SplashState : State
    {
        [Header("Assets (Addressables)")]
        [SerializeField] private AssetReferenceGameObject ApplicationFlowControllerReference;

        [Header("Configuration")] 
        [SerializeField] private int SceneIndex;
        [SerializeField] private float TimeoutDuration = 3f;

        [Header("Variables & Events")] 
        [SerializeField] private Float SceneLoadingProgress;
        [SerializeField] private GameEvent HideLoadingView;
        
        [NonSerialized] private AsyncOperation _sceneLoadingOperation;

        private ApplicationFlowController _flowControllerInstance;
        
        private const float SplashDelay = 0.5f;

        public override IEnumerator Init(IState listener)
        {
            yield return base.Init(listener);

            SceneLoadingProgress.SetValue(0);
        }

        public override IEnumerator Execute()
        {
            yield return base.Execute();
            yield return InstantiateApplicationFlowController();
            
            //Start Loading the Game Scene
            yield return LoadGameScene();
        }
        
        public override IEnumerator Tick()
        {
            yield return base.Tick();
            yield return WaitForSceneLoadAndTimeout();
            yield return FinalizeSceneActivation();
            CompleteBootSequence();
        }

        private IEnumerator InstantiateApplicationFlowController()
        {
            var handle =  Addressables.LoadAssetAsync<GameObject>(ApplicationFlowControllerReference);

            while (!handle.IsDone) yield return null;

            if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                var container = LifetimeScope.Find<RootLifetimeScope>().Container;
                _flowControllerInstance = container.Instantiate(handle.Result.GetComponent<ApplicationFlowController>());
            }
            
        }

        private IEnumerator LoadGameScene()
        {
            // Artificial delay want the splash to linger
            yield return new WaitForSeconds(SplashDelay);

            _sceneLoadingOperation = SceneManager.LoadSceneAsync(SceneIndex, LoadSceneMode.Additive);
            if (_sceneLoadingOperation != null)
                _sceneLoadingOperation.allowSceneActivation = false; // Hold until we say go
        }
        
        private IEnumerator WaitForSceneLoadAndTimeout()
        {
            float timeStarted = Time.time;

            while (_sceneLoadingOperation != null)
            {
                // Update Progress (normalized 0 to 1 based on the 0.9 cap)
                float progress = Mathf.Clamp01(_sceneLoadingOperation.progress / 0.9f);
                SceneLoadingProgress.SetValue(progress);

                bool isTimeOut = (Time.time - timeStarted) > TimeoutDuration;
                bool isSceneReady = _sceneLoadingOperation.progress >= 0.9f;

                if (isTimeOut || isSceneReady) break;

                yield return null;
            }
        }

        private IEnumerator FinalizeSceneActivation()
        {
            SceneLoadingProgress.SetValue(1.0f);

            if (_sceneLoadingOperation == null) yield break;

            // Allow the actual scene swap
            _sceneLoadingOperation.allowSceneActivation = true;
            yield return new WaitUntil(() => _sceneLoadingOperation.isDone);

            Scene loadedScene = SceneManager.GetSceneByBuildIndex(SceneIndex);
            if (loadedScene.IsValid())
            {
                SceneManager.SetActiveScene(loadedScene);
            }
        }

        private void CompleteBootSequence()
        {
            HideLoadingView.Invoke();
            _flowControllerInstance?.Boot();
        }
    }
}