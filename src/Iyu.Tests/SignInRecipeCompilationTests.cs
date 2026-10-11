using System.Text.RegularExpressions;
using Xunit;

namespace Iyu.Tests;

/// <summary>
/// The README's local sign-in recipes — a session cookie, an access token, a refresh-token pair — compile against
/// these packages as published. Each is the code a consuming application copies into its own sign-in endpoint, so a
/// recipe that names a member that does not exist, or passes the wrong type, fails in that application first.
/// </summary>
/// <remarks>
/// The recipes are read from the README, not repeated here. The fixture around them supplies only what each recipe
/// leaves to the application — a request type, a refresh-token store — and the namespaces it would declare: the ones
/// the README's namespace guidance publishes, the identity namespace, and an ASP.NET Core project's implicit usings.
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
        global using Iyu.MainServer.Identity;
        """;

    /// <summary>What a recipe leaves to the application, at its smallest.</summary>
    private const string ApplicationSide = """
        namespace Consumer;

        public sealed record LoginRequest(string Username, string Password);

        public sealed class EfRefreshTokenStore : IRefreshTokenStore
        {
            public Task InsertAsync(RefreshTokenRecord token, CancellationToken ct) => throw new NotImplementedException();
            public Task<RefreshTokenRecord?> FindByHashAsync(string tokenHash, CancellationToken ct) => throw new NotImplementedException();
            public Task<bool> TryMarkUsedAsync(Guid id, DateTimeOffset usedAt, CancellationToken ct) => throw new NotImplementedException();
            public Task<RefreshTokenRecord?> FindLatestChildAsync(Guid parentId, CancellationToken ct) => throw new NotImplementedException();
            public Task RevokeFamilyAsync(Guid familyId, DateTimeOffset revokedAt, CancellationToken ct) => throw new NotImplementedException();
            public Task RevokeSubjectAsync(string subject, DateTimeOffset revokedAt, CancellationToken ct) => throw new NotImplementedException();
        }
        """;

    private static IReadOnlyList<string> Recipes() =>
        [.. Regex.Matches(ReadmeCompilation.ReadmeText(), @"```csharp\r?\n(?<code>.*?)\r?\n```", RegexOptions.Singleline)
            .Select(m => m.Groups["code"].Value)
            .Where(code => code.Contains(RecipeMarker, StringComparison.Ordinal))];

    private static IReadOnlyList<string> CompileErrors(string recipe)
    {
        var source = string.Join(Environment.NewLine, ReadmeCompilation.PublishedGlobalUsings())
                     + Environment.NewLine + Usings + Environment.NewLine + ApplicationSide + $$"""

            public static class Recipe
            {
                public static void Map(WebApplicationBuilder builder, WebApplication app)
                {
            {{recipe}}
                }
            }
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
}
