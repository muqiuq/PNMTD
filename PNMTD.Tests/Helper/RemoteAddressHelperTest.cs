using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PNMTD.Lib.Helper;

namespace PNMTD.Tests.Helper
{
    [TestClass]
    public class RemoteAddressHelperTest
    {

        [TestMethod]
        public void RemoteAddressParseTest1()
        {
            var successfullParse = RemoteAddressHelper.TryGetRemoteIp("::ffff:178.197.218.9:332", out var parseResult);

            Assert.IsTrue(successfullParse);
            Assert.AreEqual("178.197.218.9", parseResult);
        }

        [TestMethod]
        public void RemoteAddressStripTest1()
        {
            var stripped = RemoteAddressHelper.StripPortFromRemoteAddressIfAny("::ffff:178.197.218.9:332");

            Assert.AreEqual("::ffff:178.197.218.9", stripped);
        }

        [TestMethod]
        public void RemoteAddressStripTest2()
        {
            var stripped = RemoteAddressHelper.StripPortFromRemoteAddressIfAny("178.197.218.9:0");

            Assert.AreEqual("178.197.218.9", stripped);
        }

        [TestMethod]
        public void RemoteAddressStripTest3()
        {
            var stripped = RemoteAddressHelper.StripPortFromRemoteAddressIfAny("Something Else");

            Assert.AreEqual("Something Else", stripped);
        }

    }
}
