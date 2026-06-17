using NUnit.Framework;
using ProjectCore.UI;
using ProjectGame.Core.Interfaces;

namespace ProjectCore.Tests
{
    public class ApplicationFlowTests
    {
        private IFlowLogic _logic;

        [SetUp]
        public void SetUp()
        {
            _logic = new ApplicationFlowLogic();
        }

        // ===== LEVEL FAIL CONTEXT =====
        

        [Test]
        public void LevelFail_Game_Should_GoToGame()
        {
            var result = _logic.GetDecision(FlowContext.LevelFail, UICloseReasons.Game);
            Assert.AreEqual(FlowIntent.GoToGame, result);
        }
        

        // ===== EDGE CASES =====

        [Test]
        public void InvalidContext_Should_DefaultToGame()
        {
            var result = _logic.GetDecision((FlowContext)999, UICloseReasons.Game);
            Assert.AreEqual(FlowIntent.DefaultToGame, result);
        }

        [Test]
        public void InvalidReason_Should_DefaultToGame()
        {
            var result = _logic.GetDecision(FlowContext.LevelFail, (UICloseReasons)999);
            Assert.AreEqual(FlowIntent.DefaultToGame, result);
        }

        [Test]
        public void InvalidContextAndReason_Should_DefaultToGame()
        {
            var result = _logic.GetDecision((FlowContext)999, (UICloseReasons)999);
            Assert.AreEqual(FlowIntent.DefaultToGame, result);
        }
    }
}