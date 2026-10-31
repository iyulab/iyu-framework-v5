using Microsoft.Extensions.Options;

namespace Iyu.Uncad;

/// <summary>
/// What <c>AddIyuUncad</c> is configured with.
/// </summary>
/// <remarks>
/// <c>uncad</c> is a separate program under the GPL-3.0-or-later; this package only starts it as a child process. The host
/// installs the executable and says where it is with <see cref="ExePath"/>.
/// </remarks>
public sealed class IyuUncadOptions
{
    /// <summary>
    /// The full path of the <c>uncad</c> executable the host installed. Required: there is no default, and a host that calls
    /// <c>AddIyuUncad</c> without it does not start.
    /// </summary>
    /// <remarks>
    /// It has to name the program itself. On Windows a batch file (<c>.cmd</c>, <c>.bat</c>) is run through <c>cmd.exe</c>, which
    /// parses its arguments again, so a file name could then be read as a command: do not point this at such a wrapper.
    /// A bare command name is not looked up on <c>PATH</c>; the file has to exist at this path. That is checked each time a
    /// drawing is read, not at startup, because the program can be installed after the host has started — until it is, a read
    /// ends with <see cref="UncadFailure.NotConfigured"/>.
    /// </remarks>
    public string? ExePath { get; set; }

    /// <summary>
    /// The longest one run of the program may take. When it is exceeded the process and its descendants are ended and the
    /// read ends with <see cref="UncadFailure.Timeout"/>. Must be positive: "no limit" cannot be configured, because a drawing is
    /// untrusted input and a parser can loop on it. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The most standard output one run may produce, in bytes. When it is exceeded the process and its descendants are
    /// ended and the read ends with <see cref="UncadFailure.OutputTooLarge"/>. Must be positive. Defaults to 64 MiB.
    /// </summary>
    public long MaxOutputBytes { get; set; } = 64L * 1024 * 1024;
}

/// <summary>
/// Refuses to start a host that turned <c>uncad</c> on with nothing to run, or with a limit that is no limit. Names the setting
/// that is wrong, never its value.
/// </summary>
/// <remarks>
/// A value outside the range is refused, not quietly replaced by the default. A limit that loosens itself when a setting is
/// mistyped (zero, a negative number, a value that did not parse) is the failure the limits exist to prevent, and a host that
/// is told at startup corrects it once, where one that is not runs unbounded until something goes wrong.
/// </remarks>
internal sealed class IyuUncadOptionsValidator : IValidateOptions<IyuUncadOptions>
{
    public ValidateOptionsResult Validate(string? name, IyuUncadOptions options)
    {
        List<string>? failures = null;

        if (string.IsNullOrWhiteSpace(options.ExePath))
        {
            (failures ??= []).Add(
                $"{nameof(IyuUncadOptions)}.{nameof(IyuUncadOptions.ExePath)} must be set to the full path of the uncad executable, "
                + "or AddIyuUncad must not be called.");
        }

        if (options.Timeout <= TimeSpan.Zero)
            (failures ??= []).Add($"{nameof(IyuUncadOptions)}.{nameof(IyuUncadOptions.Timeout)} must be positive.");

        if (options.MaxOutputBytes <= 0)
            (failures ??= []).Add($"{nameof(IyuUncadOptions)}.{nameof(IyuUncadOptions.MaxOutputBytes)} must be positive.");

        return failures is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
