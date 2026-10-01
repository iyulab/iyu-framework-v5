using Iyu.Data.WriteRules;
using Iyu.DocConvert;
using Iyu.FileServer;
using Iyu.MainServer;
using Iyu.MainServer.Identity;
using Iyu.Report;
using Iyu.Server.Chat;
using Iyu.Tests.Data;
using Iyu.Tests.Identity;
using Iyu.VaultAi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests;

/// <summary>
/// Every registration entry point, in the minimal setup its documentation asks for, builds a host in
/// the Development environment — where ASP.NET Core validates the whole container at <c>Build()</c>
/// (<c>ValidateOnBuild</c>, <c>ValidateScopes</c>). A service registered unconditionally with a
/// dependency the app was told it may omit passes every test that never resolves it, and stops the
/// app before its first request; this is the only place that sees it.
/// </summary>
public class ServiceProviderValidationTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static WebApplicationBuilder DevelopmentBuilder(IDictionary<string, string?>? settings = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        if (settings is not null) builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }

    private static void BuildsAndDisposes(WebApplicationBuilder builder)
    {
        using var app = builder.Build();
    }

    private static void IdentityWithItsRequiredStores(WebApplicationBuilder builder)
    {
        var store = new FakeIdentityStore();
        builder.Services.AddSingleton<IIdentityStore>(store);
        builder.Services.AddSingleton<IServiceClientStore>(store);
        builder.Services.AddIyuIdentity(new IdentityTokenOptions { SigningKey = Key }, permissionCatalog: ["orders.read"]);
    }

    [Fact]
    public void Identity_without_refresh_tokens_needs_neither_refresh_port()
    {
        var builder = DevelopmentBuilder();
        IdentityWithItsRequiredStores(builder);

        BuildsAndDisposes(builder);
    }

    [Fact]
    public void Identity_with_refresh_tokens_and_both_ports_builds()
    {
        var builder = DevelopmentBuilder();
        IdentityWithItsRequiredStores(builder);
        builder.Services.AddIyuRefreshTokens();
        builder.Services.AddSingleton<IRefreshTokenStore>(new FakeRefreshTokenStore());
        builder.Services.AddSingleton<IUserTokenClaimsSource>(new FakeUserTokenClaimsSource());

        BuildsAndDisposes(builder);
    }

    /// <summary>The opposite case: turning refresh tokens on is what makes the ports required.</summary>
    [Fact]
    public void Refresh_tokens_turned_on_without_a_port_is_refused_at_startup_naming_it()
    {
        var builder = DevelopmentBuilder();
        IdentityWithItsRequiredStores(builder);
        builder.Services.AddIyuRefreshTokens();
        builder.Services.AddSingleton<IRefreshTokenStore>(new FakeRefreshTokenStore());

        var error = Assert.Throws<AggregateException>(() => BuildsAndDisposes(builder));

        Assert.Contains(nameof(IUserTokenClaimsSource), error.Message);
    }

    [Fact]
    public void Main_server_builds()
    {
        var builder = DevelopmentBuilder();
        builder.Services.AddIyuMainServer<RuleContext>(
            configureDb: db => db.UseInMemoryDatabase(nameof(Main_server_builds)),
            configure: _ => { });

        BuildsAndDisposes(builder);
    }

    [Fact]
    public void File_gateway_builds()
    {
        var builder = DevelopmentBuilder();
        builder.Services.AddIyuFileGateway(
            gw => { gw.SigningKey = Key; },
            blob => { blob.ConnectionString = "UseDevelopmentStorage=true"; blob.ContainerName = "test"; });

        BuildsAndDisposes(builder);
    }

    [Fact]
    public void Report_builds()
    {
        var builder = DevelopmentBuilder();
        builder.Services.AddIyuReport();

        BuildsAndDisposes(builder);
    }

    [Fact]
    public void Document_conversion_builds()
    {
        var builder = DevelopmentBuilder();
        builder.Services.AddIyuDocConvert();

        BuildsAndDisposes(builder);
    }

    [Fact]
    public void Chat_enabled_builds()
    {
        var builder = DevelopmentBuilder(new Dictionary<string, string?> { ["Chat:Enabled"] = "true" });
        builder.Services.AddIyuChat(builder.Configuration);

        BuildsAndDisposes(builder);
    }

    [Fact]
    public void Write_rules_build()
    {
        var builder = DevelopmentBuilder();
        builder.Services.AddIyuWriteRules(typeof(ServiceProviderValidationTests).Assembly);

        BuildsAndDisposes(builder);
    }

    [Fact]
    public void VaultAi_reports_build()
    {
        var builder = DevelopmentBuilder(new Dictionary<string, string?> { ["VaultAi:Url"] = "http://localhost:1" });
        builder.Services.AddVaultAiReports(builder.Configuration);

        BuildsAndDisposes(builder);
    }
}
