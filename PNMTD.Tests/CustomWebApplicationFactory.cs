using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Data;
using PNMTD.Helper;
using PNMTD.Lib.Authentification;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Tests
{
    public class CustomWebApplicationFactory<TProgram>
    : WebApplicationFactory<TProgram> where TProgram : class
    {
        internal DbTestHelper DbTestHelper { get; private set; }

        public string JwtToken { get; private set; }

        

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // TODO: Change this. This is only for Global.IsDevelopment
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
            

            builder.ConfigureServices(services =>
            {
                var dbContextDescriptor = services.SingleOrDefault(
                d => d.ServiceType ==
                    typeof(PnmtdDbContext));

                services.Remove(dbContextDescriptor);

                var config = ConfigurationHelper.InitConfiguration(useMain: true);

                JwtToken = JwtTokenHelper.GenerateNewToken(config, "unittest");

                services.AddSingleton<IConfiguration>(config);

                var connectionString = "DataSource=myshareddb;mode=memory;cache=shared";
                var keepAliveConnection = new SqliteConnection(connectionString);
                keepAliveConnection.Open();

                DbTestHelper = new DbTestHelper(config);

                // Create open SqliteConnection so EF won't automatically close it.
                services.AddSingleton<DbTestHelper>(DbTestHelper);
                services.AddSingleton<PnmtdDbContext>(DbTestHelper.DbContext);
                services.AddSingleton(keepAliveConnection);

            });

            builder.UseEnvironment("Development");
        }

        protected override void ConfigureClient(HttpClient client)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", JwtToken);

            base.ConfigureClient(client);
        }
    }
}
