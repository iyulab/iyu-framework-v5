using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Refuses to start a host whose cookie sessions would be protected by a key ring that lives only inside the
/// container: outside Development, no key repository configured, running in a container.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IyuIdentityServiceCollectionExtensions.AddIyuIdentity"/> turns on cookie sessions, and a cookie is
/// only as durable as the Data Protection keys that sealed it. With no repository configured, ASP.NET Core keeps
/// the keys in the process's own profile directory — inside a container that directory goes away with the
/// container, so every redeploy signs every web user out. ASP.NET Core says so in two warning lines at startup,
/// and those lines were not enough: the configuration shipped. So this is a refusal rather than another log line.
/// </para>
/// <para>
/// "In a container" is the runtime's own signal, <c>DOTNET_RUNNING_IN_CONTAINER</c> — the one the framework's
/// container images set and ASP.NET Core reads for the same warning. Outside a container the default repository
/// is the user profile or the registry, which outlives the process, so nothing is refused there.
/// </para>
/// <para>
/// Where the keys go is the application's choice — its database
/// (<c>Microsoft.AspNetCore.DataProtection.EntityFrameworkCore</c>, <c>PersistKeysToDbContext</c>), a mounted
/// volume (<c>PersistKeysToFileSystem</c>), or another store. Any of them sets the repository this checks.
/// </para>
/// </remarks>
internal sealed class CookieKeyRingValidator(IServiceProvider services, IdentityTokenOptions tokenOptions)
    : IValidateOptions<KeyManagementOptions>
{
    internal const string ContainerVariable = "DOTNET_RUNNING_IN_CONTAINER";

    public ValidateOptionsResult Validate(string? name, KeyManagementOptions options)
    {
        if (options.XmlRepository is not null || tokenOptions.AllowContainerLocalKeyRing)
            return ValidateOptionsResult.Success;

        var environment = services.GetService<IHostEnvironment>();
        if (environment is null || environment.IsDevelopment())
            return ValidateOptionsResult.Success;

        var container = services.GetService<IConfiguration>()?[ContainerVariable]
            ?? Environment.GetEnvironmentVariable(ContainerVariable);
        if (!string.Equals(container, "true", StringComparison.OrdinalIgnoreCase))
            return ValidateOptionsResult.Success;

        return ValidateOptionsResult.Fail(
            "Cookie sessions (AddIyuIdentity) would be protected by Data Protection keys kept only inside this "
            + "container, so every redeploy would sign every web user out. Persist the key ring outside the "
            + "container — services.AddDataProtection().PersistKeysToDbContext<TContext>() (package "
            + "Microsoft.AspNetCore.DataProtection.EntityFrameworkCore), or PersistKeysToFileSystem on a mounted "
            + "volume — and set SetApplicationName. To run on a key ring that is lost with the container, set "
            + $"IdentityTokenOptions.{nameof(IdentityTokenOptions.AllowContainerLocalKeyRing)}.");
    }
}
