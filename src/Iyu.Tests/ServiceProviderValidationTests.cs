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

    /// <summary>The core every host uses: a claims source, and no store of any kind.</summary>
    private static void IdentityWithItsRequiredStores(WebApplicationBuilder builder, bool claimsSource = true)
    {
        if (claimsSource) builder.Services.AddSingleton<IUserClaimsSource>(new FakeUserClaimsSource());
        builder.Services.AddIyuIdentity(new IdentityTokenOptions { SigningKey = Key }, permissionCatalog: ["orders.read"]);
    }

    /// <summary>
    /// Sessions and access tokens need nothing but the claims source — no service-client, credential or
    /// refresh-token store.
    /// </summary>
    [Fact]
    public void Identity_alone_needs_no_store()
    {
        var builder = DevelopmentBuilder();
        IdentityWithItsRequiredStores(builder);

        BuildsAndDisposes(builder);
    }

    [Fact]
    public void Service_clients_with_their_store_build()
    {
        var builder = DevelopmentBuilder();
        IdentityWithItsRequiredStores(builder);
        builder.Services.AddIyuServiceClients();
        builder.Services.AddSingleton<IServiceClientStore>(new FakeIdentityStore());

        BuildsAndDisposes(builder);
    }

    [Fact]
    public void Service_clients_turned_on_without_a_store_is_refused_at_startup_naming_it()
    {
        var builder = DevelopmentBuilder();
        IdentityWithItsRequiredStores(builder);
        builder.Services.AddIyuServiceClients();

        var error = Assert.Throws<AggregateException>(() => BuildsAndDisposes(builder));

        Assert.Contains(nameof(IServiceClientStore), error.Message);
    }

    [Fact]
    public void User_credentials_with_their_store_build()
    {
        var builder = DevelopmentBuilder();
        IdentityWithItsRequiredStores(builder);
        builder.Services.AddIyuUserCredentials();
        builder.Services.AddSingleton<IUserCredentialStore>(new FakeIdentityStore());

        BuildsAndDisposes(builder);
    }

    [Fact]
    public void User_credentials_turned_on_without_a_store_is_refused_at_startup_naming_it()
    {
        var builder = DevelopmentBuilder();
        IdentityWithItsRequiredStores(builder);
        builder.Services.AddIyuUserCredentials();

        var error = Assert.Throws<AggregateException>(() => BuildsAndDisposes(builder));

        Assert.Contains(nameof(IUserCredentialStore), error.Message);
    }

    [Fact]
    public void Identity_with_refresh_tokens_and_both_ports_builds()
    {
        var builder = DevelopmentBuilder();
        IdentityWithItsRequiredStores(builder);
        builder.Services.AddIyuRefreshTokens();
        builder.Services.AddSingleton<IRefreshTokenStore>(new FakeRefreshTokenStore());

        BuildsAndDisposes(builder);
    }

    /// <summary>The opposite case: turning refresh tokens on is what makes the store required.</summary>
    [Fact]
    public void Refresh_tokens_turned_on_without_a_store_is_refused_at_startup_naming_it()
    {
        var builder = DevelopmentBuilder();
        IdentityWithItsRequiredStores(builder);
        builder.Services.AddIyuRefreshTokens();

        var error = Assert.Throws<AggregateException>(() => BuildsAndDisposes(builder));

        Assert.Contains(nameof(IRefreshTokenStore), error.Message);
    }

    /// <summary>
    /// The claims source is required by identity itself: a person's session is assembled from it, with
    /// or without refresh tokens.
    /// </summary>
    [Fact]
    public void Identity_without_a_claims_source_is_refused_at_startup_naming_it()
    {
        var builder = DevelopmentBuilder();
        IdentityWithItsRequiredStores(builder, claimsSource: false);

        var error = Assert.Throws<AggregateException>(() => BuildsAndDisposes(builder));

        Assert.Contains(nameof(IUserClaimsSource), error.Message);
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
