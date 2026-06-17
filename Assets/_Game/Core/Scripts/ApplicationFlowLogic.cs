using System.Collections.Generic;
using ProjectCore.UI;
using ProjectGame.Core.Interfaces;

namespace ProjectCore
{
    public class ApplicationFlowLogic : IFlowLogic
    {
        // Key: (Context + Reason) -> Value: Intent
        private readonly Dictionary<(FlowContext, UICloseReasons), FlowIntent> _strategies;

        public ApplicationFlowLogic()
        {
            _strategies = new Dictionary<(FlowContext, UICloseReasons), FlowIntent>();
            InitializeStrategies();
        }

        private void InitializeStrategies()
        {
            Add(FlowContext.Boot,UICloseReasons.Game, FlowIntent.GoToGame);
            // --- LEVEL FAIL CONTEXT ---
            Add(FlowContext.LevelFail, UICloseReasons.Game, FlowIntent.GoToGame);
        }

        // Helper to keep the dictionary cleaner
        private void Add(FlowContext ctx, UICloseReasons reason, FlowIntent intent)
        {
            _strategies[(ctx, reason)] = intent;
        }

        /// <summary>
        /// The Pure Function: Takes inputs, returns decision.
        /// </summary>
        public FlowIntent GetDecision(FlowContext context, UICloseReasons reason)
        {
            return _strategies.GetValueOrDefault((context, reason), FlowIntent.DefaultToGame);
        }
    }
}