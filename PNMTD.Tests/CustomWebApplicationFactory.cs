using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Tests
{
    public class CustomWebApplicationFactory<TProgram>
    : WebApplicationFactory<TProgram> where TProgram : class
    {
        internal DbTestHelper DbTestHelper { get; private set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                var dbContextDescriptor = services.SingleOrDefault(
                d => d.ServiceType ==
                    typeof(PnmtdDbContext));

                services.Remove(dbContextDescriptor);

                var connectionString = "DataSource=myshareddb;mode=memory;cache=shared";
                var keepAliveConnection = new SqliteConnection(connectionString);
                keepAliveConnection.Open();

                DbTestHelper = new DbTestHelper();

                // Create open SqliteConnection so EF won't automatically close it.
                services.AddSingleton<DbTestHelper>(DbTestHelper);
                services.AddSingleton<PnmtdDbContext>(DbTestHelper.DbContext);
                services.AddSingleton(keepAliveConnection);

            });

            builder.UseEnvironment("Development");
        }
    }
}
