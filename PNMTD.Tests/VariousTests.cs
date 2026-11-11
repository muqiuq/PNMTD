using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Tests
{
    [TestClass]
    public class VariousTests
    {
        private HttpClient _client;

        public static readonly TestApp _factory = new TestApp();

        [TestInitialize]
        public void Init()
        {
            _client = _factory.Client();
        }

        [TestMethod]
        public void EmptyBoolTest()
        {
            var uplinkAvailable = _factory.DbTestHelper.DbContext.GetKeyValueByEnum<bool>(Models.Enums.KeyValueKeyEnums.UNUSED);

            Assert.IsNotNull(uplinkAvailable);
            Assert.IsFalse(uplinkAvailable);
        }

    }
}
