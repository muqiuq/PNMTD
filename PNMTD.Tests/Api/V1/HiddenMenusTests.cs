using PNMTD.Api.V1.Contracts;

namespace PNMTD.Tests.Api.V1;

/// <summary>
/// Redmine__HideMenu, Mail__HideMenu and Zabbix__HideMenu: the deployment hides their drawer entries; /system/info tells the
/// web UI which, signed in only. Nothing else changes.
/// </summary>
[TestClass]
public sealed class HiddenMenusTests
{
    [TestMethod]
    public async Task TheInfoNamesTheHiddenMenus()
    {
        using var app = new TestApp(settings: new Dictionary<string, string?> { ["Redmine:HideMenu"] = "true", ["Mail:HideMenu"] = "True", ["Zabbix:HideMenu"] = "false" });
        var info = await (await app.Client(Roles.Viewer).GetAsync("/api/v1/system/info")).ReadAsync<SystemInfo>();
        CollectionAssert.AreEqual(new[] { "redmine", "mail" }, info.HiddenMenus!.ToArray());
        var anonymous = await (await app.Client().GetAsync("/api/v1/system/info")).ReadAsync<SystemInfo>();
        Assert.IsNull(anonymous.HiddenMenus, "signed out: only the identity");
    }

    [TestMethod]
    public void NoneByDefault() =>
        Assert.AreEqual(0, PNMTD.Services.Domain.SystemService.HiddenMenus(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Count);
}
