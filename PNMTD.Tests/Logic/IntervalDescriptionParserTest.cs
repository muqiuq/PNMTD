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
            var intervalDescription = IntervalDescriptionHelper.Parse("monthly on the 13");

            Assert.AreEqual(13, intervalDescription.DayOfMonth);
            Assert.AreEqual(IntervalDescriptionType.MONTHLY, intervalDescription.Type);
        }

        [TestMethod]
        [ExpectedException(typeof(IntervalDescriptionParseException))]
        public void ParseMonthly3()
        {
            IntervalDescriptionHelper.Parse("monthly on the 31");
        }

        [TestMethod]
        public void ParseDaily1()
        {
            var intervalDescription = IntervalDescriptionHelper.Parse("daily");

            Assert.AreEqual(IntervalDescriptionType.DAILY, intervalDescription.Type);
        }

        [TestMethod]
        public void ParseDaily2()
        {
            var intervalDescription = IntervalDescriptionHelper.Parse("daily at 12 o'clock");

            Assert.AreEqual(IntervalDescriptionType.DAILY, intervalDescription.Type);
            Assert.AreEqual(12, intervalDescription.HourOfDay);
        }

        [TestMethod]
        [ExpectedException(typeof(IntervalDescriptionParseException))]
        public void ParseDaily3()
        {
            IntervalDescriptionHelper.Parse("daily at 24 o'clock");
        }

    }
}
