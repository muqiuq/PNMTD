using Newtonsoft.Json;
using PNMTD.Models.Poco;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Tests
{
    internal static class TestHelper
    {
        internal static HttpContent SerializeToHttpContent(object obj)
        {
            var byteContent = new ByteArrayContent(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(obj)));
            byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return byteContent;
        }

    }
}
