using Iyu.Core.Identity;
using Iyu.MainServer.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.Identity;

/// <summary>
/// A local password check: find the account, verify the password, refuse an inactive account — every check costing
/// exactly one verification, so neither an unknown name nor an inactive account answers faster than a real one.
/// </summary>
public class UserCredentialServiceTests
{
    private static readonly PasswordHasher<IUser> Hasher = new();
    private readonly FakeIdentityStore _store = new();
    private readonly CountingHasher _hasher = new(Hasher);
    private readonly RecordingLogger<UserCredentialService> _log = new();

    private UserCredentialService Service() => new(_store, _hasher, _log);

    private Guid Account(string name, string password, bool active = true) =>
        _store.AddUser(name, name, Hasher.HashPassword(null!, password), active);

    [Fact]
    public async Task The_right_password_opens_the_account()
    {
        var id = Account("ana", "s3cret");

        var check = await Service().VerifyAsync("ana", "s3cret");

        Assert.True(check.Ok);
        Assert.Null(check.Failure);
        Assert.Equal(id, check.User!.Id);
        Assert.Null(check.UpgradedPasswordHash);
    }

    [Fact]
    public async Task A_wrong_password_is_reported_with_the_account_it_was_tried_on()
    {
        var id = Account("ana", "s3cret");

        var check = await Service().VerifyAsync("ana", "guess");

        Assert.False(check.Ok);
        Assert.Equal(CredentialFailure.WrongPassword, check.Failure);
        Assert.Equal(id, check.User!.Id);   // the application counts failures toward its lockout by this
    }

    [Theory]
    [InlineData("nobody")]
    [InlineData("")]
    [InlineData(null)]
    public async Task An_unknown_name_costs_one_verification_like_a_real_one(string? name)
    {
        Account("ana", "s3cret");

        var check = await Service().VerifyAsync(name, "s3cret");

        Assert.Equal(CredentialFailure.UnknownUser, check.Failure);
        Assert.Null(check.User);
        Assert.Equal(1, _hasher.Verifications);
    }

    [Fact]
    public async Task A_real_account_costs_the_same_one_verification()
    {
        Account("ana", "s3cret");

        await Service().VerifyAsync("ana", "guess");

        Assert.Equal(1, _hasher.Verifications);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task An_account_without_a_password_answers_like_a_wrong_one(string? storedHash)
    {
        _store.AddUser("ext", "ext", storedHash!);

        var check = await Service().VerifyAsync("ext", "");

        Assert.Equal(CredentialFailure.WrongPassword, check.Failure);
        Assert.Equal(1, _hasher.Verifications);
    }

    [Fact]
    public async Task An_inactive_account_is_told_apart_only_with_its_password()
    {
        Account("gone", "s3cret", active: false);

        var right = await Service().VerifyAsync("gone", "s3cret");
        var wrong = await Service().VerifyAsync("gone", "guess");

        Assert.Equal(CredentialFailure.Inactive, right.Failure);
        Assert.False(right.Ok);
        Assert.Equal("gone", right.User!.Username);
        Assert.Equal(CredentialFailure.WrongPassword, wrong.Failure);
        Assert.Equal(2, _hasher.Verifications);   // the inactive account was hashed both times, like any other
    }

    [Fact]
    public async Task A_hash_the_hasher_cannot_read_is_logged_and_refused_not_thrown()
    {
        _store.AddUser("legacy", "legacy", "$2a$10$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy");

        var check = await Service().VerifyAsync("legacy", "s3cret");

        Assert.Equal(CredentialFailure.UnreadableHash, check.Failure);
        Assert.Contains(_log.Messages, m => m.Contains("IPasswordHasher<IUser>"));
    }

    [Fact]
    public async Task A_hash_made_with_older_settings_comes_back_rehashed()
    {
        var weak = new PasswordHasher<IUser>(Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }));
        _store.AddUser("old", "old", weak.HashPassword(null!, "s3cret"));

        var check = await Service().VerifyAsync("old", "s3cret");

        Assert.True(check.Ok);
        Assert.NotNull(check.UpgradedPasswordHash);
        Assert.Equal(PasswordVerificationResult.Success,
            Hasher.VerifyHashedPassword(null!, check.UpgradedPasswordHash!, "s3cret"));
    }

    private static ServiceCollection Host()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IIdentityStore>(new FakeIdentityStore());
        services.AddSingleton<IServiceClientStore>(sp => (FakeIdentityStore)sp.GetRequiredService<IIdentityStore>());
        return services;
    }

    private static void AddIdentity(IServiceCollection services) => services.AddIyuIdentity(
        new IdentityTokenOptions { SigningKey = "0123456789abcdef0123456789abcdef" }, permissionCatalog: ["orders.read"]);

    [Fact]
    public void AddIyuIdentity_registers_the_check_with_ASP_NET_Core_Identitys_hasher()
    {
        var services = Host();
        AddIdentity(services);

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<UserCredentialService>());
        Assert.IsType<PasswordHasher<IUser>>(sp.GetRequiredService<IPasswordHasher<IUser>>());
    }

    [Fact]
    public void A_host_registered_hasher_survives_AddIyuIdentity()
    {
        var mine = new CountingHasher(Hasher);
        var services = Host();
        services.AddSingleton<IPasswordHasher<IUser>>(mine);
        AddIdentity(services);

        using var sp = services.BuildServiceProvider();
        Assert.Same(mine, sp.GetRequiredService<IPasswordHasher<IUser>>());
    }

    private sealed class CountingHasher(IPasswordHasher<IUser> inner) : IPasswordHasher<IUser>
    {
        public int Verifications { get; private set; }

        public string HashPassword(IUser user, string password) => inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(IUser user, string hashedPassword, string providedPassword)
        {
            Verifications++;
            return inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }
}
