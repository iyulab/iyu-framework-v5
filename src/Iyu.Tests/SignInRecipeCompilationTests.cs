using System.Text.RegularExpressions;
using Xunit;

namespace Iyu.Tests;

/// <summary>
/// The README's sign-in recipes — the three local ones (a session cookie, an access token, a refresh-token pair) and
/// the external-provider one — compile against these packages as published. Each is the code a consuming
/// application copies into its own host, so a recipe that names a member that does not exist, or passes the wrong
/// type, fails in that application first.
/// </summary>
/// <remarks>
/// The recipes are read from the README, not repeated here. The fixture around them supplies only what each recipe
/// leaves to the application — a request type, a refresh-token store, a database context with external logins — and
/// the namespaces it would declare: the ones the README's namespace guidance publishes, the identity namespace, EF
/// Core, and an ASP.NET Core project's implicit usings.
/// </remarks>
public sealed class SignInRecipeCompilationTests
{
    /// <summary>What marks a recipe as a local sign-in: it checks the password through the framework.</summary>
    private const string RecipeMarker = "credentials.VerifyAsync(";

    private const string Usings = """
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using Microsoft.AspNetCore.Builder;
        global using Microsoft.AspNetCore.Http;
        global using Microsoft.Extensions.DependencyInjection;
        global using Microsoft.EntityFrameworkCore;
        global using Iyu.MainServer.Identity;
        """;

    /// <summary>What marks the external-provider recipe: it is the application's resolver.</summary>
    private const string ExternalRecipeMarker = ": IExternalLoginResolver";

    /// <summary>What a recipe leaves to the application, at its smallest.</summary>
    private const string ApplicationSide = """
        namespace Consumer;

        public sealed record LoginRequest(string Username, string Password);

        public sealed class AppUserCredentialStore : IUserCredentialStore
        {
            public Task<Iyu.Core.Identity.IUser?> FindUserByUsernameAsync(string username, CancellationToken ct) => throw new NotImplementedException();
        }

        public sealed class EfRefreshTokenStore : IRefreshTokenStore
        {
            public Task InsertAsync(RefreshTokenRecord token, CancellationToken ct) => throw new NotImplementedException();
            public Task<RefreshTokenRecord?> FindByHashAsync(string tokenHash, CancellationToken ct) => throw new NotImplementedException();
            public Task<bool> TryMarkUsedAsync(Guid id, DateTimeOffset usedAt, CancellationToken ct) => throw new NotImplementedException();
            public Task<RefreshTokenRecord?> FindLatestChildAsync(Guid parentId, CancellationToken ct) => throw new NotImplementedException();
            public Task RevokeFamilyAsync(Guid familyId, DateTimeOffset revokedAt, CancellationToken ct) => throw new NotImplementedException();
            public Task RevokeSubjectAsync(string subject, DateTimeOffset revokedAt, CancellationToken ct) => throw new NotImplementedException();
        }

        public sealed class ExternalLoginRow
        {
            public Guid Id { get; set; }
            public string Provider { get; set; } = "";
            public string ProviderKey { get; set; } = "";
            public Guid UserId { get; set; }
        }

        public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
        {
            public DbSet<ExternalLoginRow> ExternalLogins => Set<ExternalLoginRow>();
        }
        """;

    private static IEnumerable<string> CSharpBlocks() =>
        Regex.Matches(ReadmeCompilation.ReadmeText(), @"```csharp\r?\n(?<code>.*?)\r?\n```", RegexOptions.Singleline)
            .Select(m => m.Groups["code"].Value);

    private static IReadOnlyList<string> Recipes() =>
        [.. CSharpBlocks().Where(code => code.Contains(RecipeMarker, StringComparison.Ordinal))];

    private static IReadOnlyList<string> ExternalRecipes() =>
        [.. CSharpBlocks().Where(code => code.Contains(ExternalRecipeMarker, StringComparison.Ordinal))];

    /// <summary>
    /// Compiles a recipe the way an application holds it: its statements in the host's setup, and the types it
    /// declares — a recipe may end with the application's own class — beside it. A type declaration starts at the
    /// first line that opens one at the left margin.
    /// </summary>
    private static IReadOnlyList<string> CompileErrors(string recipe)
    {
        var lines = recipe.Replace("\r\n", "\n").Split('\n');
        var firstType = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^(public|internal)\s"));
        var statements = string.Join('\n', firstType < 0 ? lines : lines[..firstType]);
        var types = firstType < 0 ? "" : string.Join('\n', lines[firstType..]);

        var source = string.Join(Environment.NewLine, ReadmeCompilation.PublishedGlobalUsings())
                     + Environment.NewLine + Usings + Environment.NewLine + ApplicationSide + $$"""

            public static class Recipe
            {
                public static void Map(WebApplicationBuilder builder, WebApplication app)
                {
            {{statements}}
                }
            }

            {{types}}
            """;
        return [.. ReadmeCompilation.Errors("SignInRecipe", source).Select(e => $"{e.Id}: {e.GetMessage()}")];
    }

    /// <summary>
    /// Guards the guard: a README reworded so no block carried the marker would leave the theory below with nothing to
    /// compile, and a theory with no rows does not fail.
    /// </summary>
    [Fact]
    public void The_README_carries_the_three_local_sign_in_recipes()
    {
        Assert.Equal(3, Recipes().Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void A_local_sign_in_recipe_compiles_as_published(int index)
    {
        var recipe = Recipes()[index];

        var errors = CompileErrors(recipe);

        Assert.True(errors.Count == 0,
            $"the README's local sign-in recipe #{index + 1} does not compile against the packages:\n  "
            + string.Join("\n  ", errors) + "\n\n" + recipe);
    }

    /// <summary>
    /// The compile has to be able to fail, or its passing says nothing about the recipes: a member the API does not
    /// have is reported, not tolerated.
    /// </summary>
    [Fact]
    public void A_recipe_naming_a_member_that_does_not_exist_does_not_compile()
    {
        var broken = Recipes()[0].Replace(RecipeMarker, "credentials.VerifyPasswordAsync(", StringComparison.Ordinal);

        Assert.Contains(CompileErrors(broken), e => e.StartsWith("CS1061", StringComparison.Ordinal));
    }

    [Fact]
    public void The_external_provider_recipe_compiles_as_published()
    {
        var recipe = Assert.Single(ExternalRecipes());

        var errors = CompileErrors(recipe);

        Assert.True(errors.Count == 0,
            "the README's external-provider recipe does not compile against the packages:\n  "
            + string.Join("\n  ", errors) + "\n\n" + recipe);
    }

    /// <summary>
    /// The same control for the external recipe — its resolver must still implement the port: a method named
    /// differently from the interface's leaves the interface unimplemented.
    /// </summary>
    [Fact]
    public void An_external_recipe_whose_resolver_misses_the_port_does_not_compile()
    {
        var broken = Assert.Single(ExternalRecipes())
            .Replace("ResolveSubjectAsync(", "ResolveAccountAsync(", StringComparison.Ordinal);

        Assert.Contains(CompileErrors(broken), e => e.StartsWith("CS0535", StringComparison.Ordinal));
    }
}
