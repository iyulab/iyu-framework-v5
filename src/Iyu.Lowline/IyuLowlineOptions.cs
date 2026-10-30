using Lowline;
using Microsoft.Extensions.Options;

namespace Iyu.Lowline;

/// <summary>
/// What <c>AddIyuLowline</c> is configured with. The engine's own options stay the engine's: the forms are defined on
/// <see cref="Engine"/>, which refuses a definition it cannot resolve at the moment it is given, so a host with a
/// form the engine does not take does not start.
/// </summary>
public sealed class IyuLowlineOptions
{
    /// <summary>
    /// The engine's options — define the forms with <see cref="LowlineOptions.Form"/> and, to fill a form from what a
    /// person wrote, give a model with <see cref="LowlineOptions.UseModel"/>. The store is the application's own
    /// tables (<c>AddIyuLowline</c>'s type arguments); <see cref="LowlineOptions.UseStore"/> is not for the caller.
    /// </summary>
    public LowlineOptions Engine { get; } = new();

    /// <summary>
    /// The longest draft (the caller's account of what was shown while a record was written) a settle request may carry,
    /// in characters. A longer one is refused before anything is read. Defaults to 65,536.
    /// </summary>
    public int MaxDraftLength { get; set; } = 64 * 1024;

    /// <summary>
    /// The authorization policy the endpoints require. <c>null</c> (the default) requires a signed-in caller, which is
    /// the least that is accepted: the endpoints have no anonymous mode.
    /// </summary>
    public string? Policy { get; set; }

    /// <summary>The largest request body an endpoint reads, in bytes. A larger one is refused with 413. Defaults to 256 KiB.</summary>
    public int MaxRequestBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// How often the recovery pass runs, forgetting the records whose source is gone. <see cref="TimeSpan.Zero"/> turns
    /// it off. A record's source can be deleted and the engine keep answering from it for up to this long. Defaults to one hour.
    /// </summary>
    public TimeSpan RecoveryInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The largest share of a scope's records (from ten records up) one recovery pass will forget. A pass that would forget
    /// more leaves the scope alone and logs an error: forgetting cannot be undone, and so many missing sources more likely
    /// mean a source listing that came back short than a source that emptied. Defaults to 0.5; 1 turns the guard off.
    /// </summary>
    public double MaxForgetFraction { get; set; } = 0.5;
}

/// <summary>
/// Refuses to start a host that turned Lowline on without a form to run it on. Names the setting that is missing,
/// never a value.
/// </summary>
internal sealed class IyuLowlineOptionsValidator : IValidateOptions<IyuLowlineOptions>
{
    public ValidateOptionsResult Validate(string? name, IyuLowlineOptions options)
    {
        if (options.Engine.Forms.Count == 0)
        {
            return ValidateOptionsResult.Fail(
                "AddIyuLowline was called but no form is defined. Define at least one with "
                + $"{nameof(IyuLowlineOptions)}.{nameof(IyuLowlineOptions.Engine)}.{nameof(LowlineOptions.Form)}(name, definition), "
                + "or do not call AddIyuLowline.");
        }

        if (options.RecoveryInterval < TimeSpan.Zero)
            return ValidateOptionsResult.Fail($"{nameof(IyuLowlineOptions)}.{nameof(IyuLowlineOptions.RecoveryInterval)} must not be negative.");
        if (!(options.MaxForgetFraction > 0 && options.MaxForgetFraction <= 1))
            return ValidateOptionsResult.Fail($"{nameof(IyuLowlineOptions)}.{nameof(IyuLowlineOptions.MaxForgetFraction)} must be above 0 and at most 1.");

        return ValidateOptionsResult.Success;
    }
}
