using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.Identity;

/// <summary>
/// Cookie sessions over a key ring that lives only in the container are refused at startup — outside Development,
/// with no key repository configured. Every other combination starts.
/// </summary>
public sealed class CookieKeyRingTests : IDisposable
{
    private readonly string _keys = Path.Combine(Path.GetTempPath(), "iyu-keyring-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_keys)) Directory.Delete(_keys, recursive: true);
    }

    private static async Task<Exception?> StartAsync(
        string environment, bool inContainer, Action<IServiceCollection>? configure = null, bool allowLocal = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Configuration["DOTNET_RUNNING_IN_CONTAINER"] = inContainer ? "true" : null;
        builder.Services.AddSingleton<IIdentityStore>(new FakeIdentityStore());
        builder.Services.AddSingleton<IServiceClientStore>(sp => (FakeIdentityStore)sp.GetRequiredService<IIdentityStore>());
        builder.Services.AddIyuIdentity(
            new IdentityTokenOptions
            {
                SigningKey = "0123456789abcdef0123456789abcdef",
                AllowContainerLocalKeyRing = allowLocal,
            },
            permissionCatalog: ["orders.read"]);
        configure?.Invoke(builder.Services);

        await using var app = builder.Build();
        var error = await Record.ExceptionAsync(() => app.StartAsync());
        if (error is null) await app.StopAsync();
        return error;
    }

    [Fact]
    public async Task A_container_outside_Development_with_no_key_repository_is_refused_and_told_how_to_fix_it()
    {
        var error = await StartAsync(Environments.Production, inContainer: true);

        var refused = Assert.IsType<OptionsValidationException>(error);
        Assert.Contains("PersistKeysToDbContext", refused.Message);
        Assert.Contains(nameof(IdentityTokenOptions.AllowContainerLocalKeyRing), refused.Message);
    }

    [Fact]
    public async Task A_persisted_key_ring_starts()
        => Assert.Null(await StartAsync(Environments.Production, inContainer: true,
            s => s.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(_keys))));

    [Fact]
    public async Task The_opt_out_starts()
        => Assert.Null(await StartAsync(Environments.Production, inContainer: true, allowLocal: true));

    [Fact]
    public async Task Outside_a_container_starts()
        => Assert.Null(await StartAsync(Environments.Production, inContainer: false));

    [Fact]
    public async Task Development_starts()
        => Assert.Null(await StartAsync(Environments.Development, inContainer: true));
}
