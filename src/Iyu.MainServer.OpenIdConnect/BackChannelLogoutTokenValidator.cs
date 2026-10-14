using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Iyu.MainServer.Identity;

/// <summary>
/// Decides whether a logout token is the word of the provider behind an OpenID Connect handler, and which of that
/// provider's sessions it ends — OpenID Connect Back-Channel Logout 1.0, section 2.6.
/// </summary>
/// <remarks>
/// Everything the token is checked against comes from the handler's own options: the provider's issuer and signing
/// keys (its discovery document), the client id it was registered with, and the validation parameters the
/// application gave the handler. Nothing about the provider is configured a second time.
/// </remarks>
internal sealed class BackChannelLogoutTokenValidator
{
    /// <summary>The member of the <c>events</c> claim that makes a signed token a logout token.</summary>
    internal const string LogoutEvent = "http://schemas.openid.net/event/backchannel-logout";

    private readonly BackChannelLogoutOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<BackChannelLogoutTokenValidator> _log;

    public BackChannelLogoutTokenValidator(BackChannelLogoutOptions options, TimeProvider clock,
        ILogger<BackChannelLogoutTokenValidator> log)
    {
        _options = options; _clock = clock; _log = log;
    }

    /// <param name="SessionId">The provider's session the token ends — its <c>sid</c>. <c>null</c> when refused.</param>
    /// <param name="Refusal">Why the token was refused, in words fit for the provider to read. <c>null</c> when accepted.</param>
    internal readonly record struct Outcome(string? SessionId, string? Refusal)
    {
        public static Outcome Refuse(string why) => new(null, why);
    }

    public async Task<Outcome> ValidateAsync(OpenIdConnectOptions handler, string logoutToken, CancellationToken ct)
    {
        // The provider's issuer and keys, as the handler itself reads them. A provider that cannot be reached is this
        // application's failure to answer, not a bad token — it is left to surface as one.
        var provider = handler.Configuration
                       ?? (handler.ConfigurationManager is { } manager ? await manager.GetConfigurationAsync(ct) : null)
                       ?? throw new InvalidOperationException(
                           "The OpenID Connect handler has no provider configuration — neither Configuration nor a "
                           + "ConfigurationManager (set Authority or MetadataAddress).");

        var parameters = Parameters(handler, provider);

        // A key the provider's document does not list: the provider has rolled its keys since it was read. Read it again
        // first, as the handler does for an identity token (RefreshOnIssuerKeyNotFound) — otherwise every notice is
        // refused until the document's next scheduled refresh, and no session ends meanwhile. Decided from the token's
        // key id, not from the failure: for a token with no expiry the library reports an unknown key as an expired token.
        if (NamesAnUnlistedKey(logoutToken, parameters)
            && handler.RefreshOnIssuerKeyNotFound && handler.Configuration is null
            && handler.ConfigurationManager is { } refreshable)
        {
            refreshable.RequestRefresh();
            parameters = Parameters(handler, await refreshable.GetConfigurationAsync(ct));
        }

        var result = await Handler.ValidateTokenAsync(logoutToken, parameters);
        if (!result.IsValid || result.SecurityToken is not JsonWebToken token)
        {
            _log.LogWarning(result.Exception, "back-channel logout refused: the token did not validate");
            return Outcome.Refuse(result.Exception switch
            {
                SecurityTokenExpiredException when CarriesExpiry(logoutToken) => "The logout token has expired.",
                SecurityTokenNoExpirationException => "The logout token carries no expiry (exp).",
                SecurityTokenInvalidIssuerException => "The logout token was not issued by this provider.",
                SecurityTokenInvalidAudienceException => "The logout token is not addressed to this client.",
                // The library reports a refused algorithm as a bad signature; the header says which it was.
                SecurityTokenInvalidSignatureException when SignedWithUnlistedAlgorithm(logoutToken, parameters)
                    => "The logout token is signed with an algorithm this provider does not sign with.",
                _ => "The logout token is not a token signed by this provider.",
            });
        }

        // An identity token carries a nonce (when one was sent) and no logout event: neither can pass for a notice.
        if (token.TryGetPayloadValue<object>("nonce", out _))
            return Refused("The logout token carries a nonce.");
        if (!token.TryGetPayloadValue<JsonElement>("events", out var events) || events.ValueKind != JsonValueKind.Object
            || !events.TryGetProperty(LogoutEvent, out var logout) || logout.ValueKind != JsonValueKind.Object)
            return Refused("The token is not a logout token: its events claim does not name the back-channel logout event.");

        if (!token.TryGetPayloadValue<long>("iat", out var issuedAtSeconds))
            return Refused("The logout token does not say when it was issued (iat).");
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(issuedAtSeconds);
        var now = _clock.GetUtcNow();
        if (issuedAt > now + parameters.ClockSkew)
            return Refused("The logout token is dated in the future.");
        if (!token.TryGetPayloadValue<long>("exp", out _)
            && now - issuedAt > _options.MaxAgeWithoutExpiration + parameters.ClockSkew)
            return Refused("The logout token carries no expiry and was issued too long ago.");

        // A token that names a person and no session asks for every session of that person to end. Sessions here are
        // not kept by the provider's subject, so that request cannot be honoured — and is not answered as if it were.
        if (!token.TryGetPayloadValue<string>("sid", out var sessionId) || string.IsNullOrEmpty(sessionId))
            return Refused("The logout token names no session (sid). Register the client with "
                           + "backchannel_logout_session_required.");

        return new Outcome(sessionId, null);
    }

    /// <summary>Read with the claims' own names: what an application maps inbound claims to is not this check's concern.</summary>
    private static JsonWebTokenHandler Handler => new() { MapInboundClaims = false };

    /// <summary>What a logout token from <paramref name="provider"/> is checked against.</summary>
    private TokenValidationParameters Parameters(OpenIdConnectOptions handler, OpenIdConnectConfiguration provider)
    {
        var parameters = handler.TokenValidationParameters.Clone();
        string[] issuer = [provider.Issuer];
        parameters.ValidIssuers = parameters.ValidIssuers?.Concat(issuer) ?? issuer;
        parameters.IssuerSigningKeys = parameters.IssuerSigningKeys?.Concat(provider.SigningKeys) ?? provider.SigningKeys;
        if (string.IsNullOrEmpty(parameters.ValidAudience) && parameters.ValidAudiences is null)
            parameters.ValidAudience = handler.ClientId;
        parameters.ValidateIssuer = true;
        parameters.ValidateAudience = true;
        parameters.ValidateIssuerSigningKey = true;
        parameters.RequireSignedTokens = true;                      // an unsigned token (alg none) is never a provider's word
        // Signed with an algorithm the application declared for this provider's tokens, or else one the provider says
        // it signs identity tokens with. A provider that advertises none leaves it to its keys, as the handler does.
        if (parameters.ValidAlgorithms is null && provider.IdTokenSigningAlgValuesSupported.Count > 0)
            parameters.ValidAlgorithms = provider.IdTokenSigningAlgValuesSupported;
        parameters.ValidateLifetime = true;                         // holds a token to its expiry when it carries one
        parameters.RequireExpirationTime = _options.RequireExpiration;
        return parameters;
    }

    private static bool NamesAnUnlistedKey(string logoutToken, TokenValidationParameters parameters)
        => new JsonWebTokenHandler().CanReadToken(logoutToken)
           && new JsonWebToken(logoutToken).Kid is { Length: > 0 } kid
           && !(parameters.IssuerSigningKeys ?? []).Any(k => string.Equals(k.KeyId, kid, StringComparison.Ordinal));

    private static bool CarriesExpiry(string logoutToken)
        => new JsonWebTokenHandler().CanReadToken(logoutToken) && new JsonWebToken(logoutToken).TryGetPayloadValue<long>("exp", out _);

    private static bool SignedWithUnlistedAlgorithm(string logoutToken, TokenValidationParameters parameters)
        => parameters.ValidAlgorithms is { } listed
           && new JsonWebTokenHandler().CanReadToken(logoutToken)
           && !listed.Contains(new JsonWebToken(logoutToken).Alg, StringComparer.Ordinal);

    private Outcome Refused(string why)
    {
        _log.LogWarning("back-channel logout refused: {Reason}", why);
        return Outcome.Refuse(why);
    }
}
