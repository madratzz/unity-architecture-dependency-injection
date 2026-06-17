using System;
using System.Collections.Generic;
using UnityEngine;
using ProjectCore.Events;
using ProjectCore.StateMachine;
using ProjectCore.UI;
using ProjectGame.Core.Interfaces;
using VContainer;

namespace ProjectCore
{
    public class ApplicationFlowController : MonoBehaviour
    {
        [Header("Transitions (The Destinations)")]
        [SerializeField] private Transition GameStateTransition;
        [SerializeField] private Transition LevelFailTransition;
        [SerializeField] private Transition SettingsTransition;

        [Header("Events (The Triggers)")]
        [SerializeField] private GameEvent GotoGame;
        [SerializeField] private GameEvent GotoLevelFail;
        
        [Header("View Closed Events")]
        [SerializeField] private GameEventWithInt LevelFailViewClosed;
        
        // Internal Systems
        private FiniteStateMachine _stateMachine;
        private IFlowLogic _logicBrain;
        private Dictionary<FlowIntent, Action> _commandMap;
        private Camera _mainCamera;

        // ---------------------------------------------------------
        // INITIALIZATION
        // ---------------------------------------------------------
        
        [Inject]
        public void Construct(IFlowLogic logicBrain, FiniteStateMachine stateMachine)
        {
            _logicBrain = logicBrain;
            _stateMachine = stateMachine;
        }
        
        private void Awake()
        {
            _mainCamera = Camera.main;
            
            InitializeCommands();
            SubscribeEvents();
        }

        private void OnDestroy()
        {
            UnsubscribeEvents();
        }

        public void Boot()
        {
            Debug.Log("[Flow] Booting Application...");
            ResolveDecision(FlowContext.Boot, UICloseReasons.Game);
        }

        
        // Maps Intents to specific Transition Assets.
        private void InitializeCommands()
        {
            _commandMap = new Dictionary<FlowIntent, Action>
            {
                // 1. Map Intents to specific Transitions (Explicit Binding)
                { FlowIntent.GoToGame,          () => PerformTransition(GameStateTransition) },
                { FlowIntent.GoToLevelFail,     () => PerformTransition(LevelFailTransition) },

                // 2. Map Intents to Logic
                { FlowIntent.ResumePrevious,    () => _stateMachine.ShouldResumePreviousState() },
                
                // 3. Defaults
                { FlowIntent.DefaultToGame, () => PerformTransition(GameStateTransition) }
            };
        }

        // ---------------------------------------------------------
        // LOGIC RESOLUTION & EXECUTION
        // ---------------------------------------------------------

        private void ResolveDecision(FlowContext context, UICloseReasons reason)
        {
            FlowIntent intent = _logicBrain.GetDecision(context, reason);
            ExecuteIntent(intent);
        }

        private void ExecuteIntent(FlowIntent intent)
        {
            if (_commandMap.TryGetValue(intent, out Action command))
            {
                command.Invoke();
            }
            else
            {
                Debug.LogError($"[Flow] Missing binding for Intent: {intent}. Using Fallback.");
                _commandMap[FlowIntent.DefaultToGame]?.Invoke();
            }
        }

        private void PerformTransition(Transition transition)
        {
            if (transition == null)
            {
                Debug.LogWarning("[Flow] CheckTransition: Null transition requested.");
                return;
            }

            // Dependency Injection
            if (transition is UIViewTransition viewTransition)
            {
                if (_mainCamera == null) _mainCamera = Camera.main;
                viewTransition.Camera = _mainCamera;
            }

            _stateMachine.Transition(transition);
        }

        // ---------------------------------------------------------
        // EVENT HANDLERS & WIRING
        // ---------------------------------------------------------

        private void OnGotoGame() => ExecuteIntent(FlowIntent.GoToGame);
        private void OnGotoLevelFail() => ExecuteIntent(FlowIntent.GoToLevelFail);
        
        private void OnLevelFailViewClose(int value) => ResolveDecision(FlowContext.LevelFail, (UICloseReasons)value);
        

        private void SubscribeEvents()
        {
            if (GotoGame) GotoGame.Handler += OnGotoGame;
            if (GotoLevelFail) GotoLevelFail.Handler += OnGotoLevelFail;
            
            if (LevelFailViewClosed) LevelFailViewClosed.Handler += OnLevelFailViewClose;
        }

        private void UnsubscribeEvents()
        {
            if (GotoGame) GotoGame.Handler -= OnGotoGame;
            if (GotoLevelFail) GotoLevelFail.Handler -= OnGotoLevelFail;
            
            if (LevelFailViewClosed) LevelFailViewClosed.Handler -= OnLevelFailViewClose;
        }
    }
}