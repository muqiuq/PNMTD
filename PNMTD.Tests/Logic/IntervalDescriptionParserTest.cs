using PNMTD.Lib.Logic.IntervalDescriptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Tests.Logic
{
    [TestClass]
    public class IntervalDescriptionParserTest
    {

        [TestMethod]
        public void ParseWeekly()
        {
            var intervalDescription = IntervalDescriptionHelper.Parse("weekly every monday");

            Assert.AreEqual(DayOfWeek.Monday, intervalDescription.DayOfWeek);
            Assert.AreEqual(IntervalDescriptionType.WEEKLY, intervalDescription.Type);
        }

        [TestMethod]
        public void ParseMonthly1()
        {
            var intervalDescription = IntervalDescriptionHelper.Parse("every month on the 23");

            Assert.AreEqual(23, intervalDescription.DayOfMonth);
            Assert.AreEqual(IntervalDescriptionType.MONTHLY, intervalDescription.Type);
        }

        [TestMethod]
        public void ParseMonthly2()
        {
            var intervalDescription = IntervalDescriptionHelper.Parse("monthly on the 29");

            Assert.AreEqual(29, intervalDescription.DayOfMonth);
            Assert.AreEqual(IntervalDescriptionType.MONTHLY, intervalDescription.Type);
        }

        [TestMethod]
        public void ParseDaily()
        {
            var intervalDescription = IntervalDescriptionHelper.Parse("daily");

            Assert.AreEqual(IntervalDescriptionType.DAILY, intervalDescription.Type);
        }

    }
}
