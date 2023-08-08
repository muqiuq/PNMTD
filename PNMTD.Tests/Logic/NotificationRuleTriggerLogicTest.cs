using PNMTD.Lib.Logic;
using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.Models.Poco;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Tests.Logic
{
    [TestClass]
    public class NotificationRuleTriggerLogicTest
    {

        public const int SUCCESS_STATUS_CODE = EventEntityPoco.END_OF_SUCCESS_CODES - 10;
        public const int ANOTHER_SUCCESS_STATUS_CODE = EventEntityPoco.END_OF_SUCCESS_CODES - 20;
        public const int FAILURE_STATUS_CODE = EventEntityPoco.END_OF_SUCCESS_CODES + 10;
        public const int ANOTHER_FAILURE_STATUS_CODE = EventEntityPoco.END_OF_SUCCESS_CODES + 20;

        [TestMethod]
        public void ALWAYS()
        {
            Assert.IsTrue(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ALWAYS, SUCCESS_STATUS_CODE, SUCCESS_STATUS_CODE));
            Assert.IsTrue(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ALWAYS, SUCCESS_STATUS_CODE, FAILURE_STATUS_CODE));
            Assert.IsTrue(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ALWAYS, FAILURE_STATUS_CODE, FAILURE_STATUS_CODE));
            Assert.IsTrue(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ALWAYS, FAILURE_STATUS_CODE, SUCCESS_STATUS_CODE));
        }

        [TestMethod]
        public void ONLY_ON_CHANGE()
        {
            Assert.IsFalse(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_ON_CHANGE, SUCCESS_STATUS_CODE, SUCCESS_STATUS_CODE));
            Assert.IsTrue(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_ON_CHANGE, SUCCESS_STATUS_CODE, FAILURE_STATUS_CODE));
            Assert.IsFalse(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_ON_CHANGE, FAILURE_STATUS_CODE, FAILURE_STATUS_CODE));
            Assert.IsTrue(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_ON_CHANGE, FAILURE_STATUS_CODE, SUCCESS_STATUS_CODE));
            Assert.IsTrue(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_ON_CHANGE, SUCCESS_STATUS_CODE, ANOTHER_SUCCESS_STATUS_CODE));
            Assert.IsTrue(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_ON_CHANGE, FAILURE_STATUS_CODE, ANOTHER_FAILURE_STATUS_CODE));
        }

        [TestMethod]
        public void ONLY_FAILURES()
        {
            Assert.IsFalse(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_FAILURES, SUCCESS_STATUS_CODE, SUCCESS_STATUS_CODE));
            Assert.IsTrue(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_FAILURES, SUCCESS_STATUS_CODE, FAILURE_STATUS_CODE));
            Assert.IsTrue(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_FAILURES, FAILURE_STATUS_CODE, FAILURE_STATUS_CODE));
            Assert.IsFalse(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_FAILURES, FAILURE_STATUS_CODE, SUCCESS_STATUS_CODE));
            Assert.IsFalse(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_FAILURES, SUCCESS_STATUS_CODE, ANOTHER_SUCCESS_STATUS_CODE));
            Assert.IsTrue(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_FAILURES, FAILURE_STATUS_CODE, ANOTHER_FAILURE_STATUS_CODE));
        }

        [TestMethod]
        public void ONLY_FAILURES_ON_CHANGE()
        {
            Assert.IsFalse(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_FAILURES_ON_CHANGE, SUCCESS_STATUS_CODE, SUCCESS_STATUS_CODE));
            Assert.IsTrue(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_FAILURES_ON_CHANGE, SUCCESS_STATUS_CODE, FAILURE_STATUS_CODE));
            Assert.IsFalse(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_FAILURES_ON_CHANGE, FAILURE_STATUS_CODE, FAILURE_STATUS_CODE));
            Assert.IsFalse(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_FAILURES_ON_CHANGE, FAILURE_STATUS_CODE, SUCCESS_STATUS_CODE));
            Assert.IsFalse(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_FAILURES_ON_CHANGE, SUCCESS_STATUS_CODE, ANOTHER_SUCCESS_STATUS_CODE));
            Assert.IsTrue(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_FAILURES_ON_CHANGE, FAILURE_STATUS_CODE, ANOTHER_FAILURE_STATUS_CODE));
        }

        [TestMethod]
        public void ONLY_FAILURES_ON_CHANGE_AND_BACK_TO_OK()
        {
            Assert.IsFalse(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_FAILURES_ON_CHANGE_AND_BACK_TO_OK, SUCCESS_STATUS_CODE, SUCCESS_STATUS_CODE));
            Assert.IsTrue(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_FAILURES_ON_CHANGE_AND_BACK_TO_OK, SUCCESS_STATUS_CODE, FAILURE_STATUS_CODE));
            Assert.IsFalse(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_FAILURES_ON_CHANGE_AND_BACK_TO_OK, FAILURE_STATUS_CODE, FAILURE_STATUS_CODE));
            Assert.IsTrue(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_FAILURES_ON_CHANGE_AND_BACK_TO_OK, FAILURE_STATUS_CODE, SUCCESS_STATUS_CODE));
            Assert.IsFalse(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_FAILURES_ON_CHANGE_AND_BACK_TO_OK, SUCCESS_STATUS_CODE, ANOTHER_SUCCESS_STATUS_CODE));
            Assert.IsTrue(NotificationRuleTriggerLogic.Eval(NotificationRuleType.ONLY_FAILURES_ON_CHANGE_AND_BACK_TO_OK, FAILURE_STATUS_CODE, ANOTHER_FAILURE_STATUS_CODE));
        }

    }
}
