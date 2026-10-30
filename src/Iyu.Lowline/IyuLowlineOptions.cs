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

        return ValidateOptionsResult.Success;
    }
}
