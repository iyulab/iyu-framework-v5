using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Iyu.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Checks a local username and password — the step before <see cref="UserSessionService.SignInAsync"/> or
/// <see cref="UserAccessTokenService.IssueAsync"/> for an application that keeps its own accounts.
/// </summary>
/// <remarks>
/// <para>
/// One call does the three things a hand-written check gets subtly wrong: it finds the account
/// (<see cref="IIdentityStore.FindUserByUsernameAsync"/>), verifies the password against
/// <see cref="IUser.PasswordHash"/> with the registered <see cref="IPasswordHasher{TUser}"/>, and refuses an account
/// that is not <see cref="IUser.IsActive"/>. What follows a check — counting failures toward a lockout, recording
/// <see cref="IUser.LastLoginAt"/>, asking for a new password — stays the application's; the result says why a
/// check failed so it can.
/// </para>
/// <para>
/// <b>Every check costs one password verification</b>, whether or not the account exists and whether or not it has
/// a password: an unknown name is verified against a placeholder hash. A check that answered an unknown name
/// without hashing would answer it measurably faster, and the time would tell a caller which names are accounts.
/// </para>
/// <para>
/// <b>The password is verified before the account's state is read.</b> An inactive account is reported only to a
/// caller who knew its password; anyone else sees a wrong password. Reading the state first would also skip the
/// hash for inactive accounts, and the time saved would tell which accounts are inactive. (ASP.NET Core Identity's
/// sign-in manager checks the account first; this differs on purpose.)
/// </para>
/// <para>
/// The hasher is ASP.NET Core Identity's <see cref="PasswordHasher{TUser}"/> unless the application registers its
/// own <see cref="IPasswordHasher{TUser}"/> of <see cref="IUser"/> — which it must when its stored hashes are in
/// another format. The same hasher is what the application uses to hash a password it stores.
/// </para>
/// </remarks>
public sealed class UserCredentialService
{
    private readonly IIdentityStore _store;
    private readonly IPasswordHasher<IUser> _hasher;
    private readonly ILogger<UserCredentialService> _log;

    public UserCredentialService(IIdentityStore store, IPasswordHasher<IUser> hasher, ILogger<UserCredentialService> log)
    {
        _store = store; _hasher = hasher; _log = log;
    }

    /// <summary>Checks <paramref name="password"/> for the account named <paramref name="username"/>.</summary>
    /// <param name="username">The name the person signs in with. Empty is an unknown name, not an error.</param>
    /// <param name="password">The password they gave. <c>null</c> is checked as an empty password.</param>
    /// <param name="ct">Cancels the account lookup.</param>
    public async Task<CredentialCheck> VerifyAsync(string? username, string? password, CancellationToken ct = default)
    {
        password ??= "";
        var user = string.IsNullOrWhiteSpace(username) ? null : await _store.FindUserByUsernameAsync(username, ct);

        if (user is null || string.IsNullOrEmpty(user.PasswordHash))
        {
            // An account with no password (signed in only through an external provider) is answered the same way,
            // in the same time, as a name that is not an account.
            _hasher.VerifyHashedPassword(Placeholder.Instance, PlaceholderHash(), password);
            return user is null
                ? CredentialCheck.Failed(CredentialFailure.UnknownUser)
                : CredentialCheck.Failed(CredentialFailure.WrongPassword, user);
        }

        PasswordVerificationResult verified;
        try
        {
            verified = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        }
        catch (FormatException)
        {
            _log.LogError(
                "the stored password hash of account {UserId} is not in the registered hasher's format — register an "
                + "IPasswordHasher<IUser> that reads it", user.Id);
            return CredentialCheck.Failed(CredentialFailure.UnreadableHash, user);
        }

        if (verified == PasswordVerificationResult.Failed)
            return CredentialCheck.Failed(CredentialFailure.WrongPassword, user);
        if (!user.IsActive)
            return CredentialCheck.Failed(CredentialFailure.Inactive, user);

        return CredentialCheck.Succeeded(user,
            verified == PasswordVerificationResult.SuccessRehashNeeded ? _hasher.HashPassword(user, password) : null);
    }

    // One placeholder per hasher instance: hashing it again on every unknown name would make that path twice as slow
    // as a real check — the difference the placeholder exists to remove. This holds for the singleton hasher
    // AddIyuIdentity registers; a hasher registered per request would pay the hash each time.
    private static readonly ConditionalWeakTable<IPasswordHasher<IUser>, string> PlaceholderHashes = new();

    private string PlaceholderHash() => PlaceholderHashes.GetValue(_hasher,
        h => h.HashPassword(Placeholder.Instance, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

    private sealed class Placeholder : IUser
    {
        public static readonly Placeholder Instance = new();
        public Guid Id => Guid.Empty;
        public string Username => "";
        public string PasswordHash => "";
        public string DisplayName => "";
        public string? Email => null;
        public bool IsActive => false;
        public DateTimeOffset? LastLoginAt => null;
    }
}

/// <summary>The outcome of <see cref="UserCredentialService.VerifyAsync"/>.</summary>
/// <param name="User">
/// The account — on success, and on any failure where the name was an account (so the application can count failures
/// toward its lockout or tell an inactive person why). Never expose it to the caller of a failed sign-in.
/// </param>
/// <param name="Failure">Why the check failed; <c>null</c> when it succeeded.</param>
/// <param name="UpgradedPasswordHash">
/// On success, when the stored hash was made with older settings than the hasher uses now: the same password hashed
/// with the current ones. Store it in place of <see cref="IUser.PasswordHash"/>; nothing here writes to the store.
/// </param>
public sealed record CredentialCheck(IUser? User, CredentialFailure? Failure, string? UpgradedPasswordHash)
{
    /// <summary>The password is the account's and the account is active.</summary>
    public bool Ok => Failure is null;

    internal static CredentialCheck Succeeded(IUser user, string? upgradedHash) => new(user, null, upgradedHash);
    internal static CredentialCheck Failed(CredentialFailure failure, IUser? user = null) => new(user, failure, null);
}

/// <summary>Why <see cref="UserCredentialService.VerifyAsync"/> refused a sign-in. Answer the person the same way for all.</summary>
public enum CredentialFailure
{
    /// <summary>No account has that name.</summary>
    UnknownUser,

    /// <summary>The password is not the account's — or the account has none.</summary>
    WrongPassword,

    /// <summary>The password is right, but the account is not active.</summary>
    Inactive,

    /// <summary>
    /// The account's stored hash is not in a format the registered hasher reads. A configuration fault, logged as an
    /// error — not a wrong password, so it is not counted toward a lockout.
    /// </summary>
    UnreadableHash,
}
