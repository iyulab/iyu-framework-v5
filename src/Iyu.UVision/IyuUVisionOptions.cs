using Microsoft.Extensions.Options;

namespace Iyu.UVision;

/// <summary>
/// What <c>AddIyuUVision</c> is configured with.
/// </summary>
/// <remarks>
/// The engine is U-Vision, a separate self-hosted service. This package only calls its HTTP interface; the host runs the engine
/// and says where it is with <see cref="BaseUrl"/>.
/// </remarks>
public sealed class IyuUVisionOptions
{
    /// <summary>
    /// The address of the engine's API, <b>including the base path the engine is mounted at</b> — <c>http://host:8000/api/u-vision</c>
    /// for an engine on its default path, not just <c>http://host:8000</c>. Judging posts to <c>{BaseUrl}/inspect</c>. Required: there is no
    /// default, and a host that calls <c>AddIyuUVision</c> without it does not start.
    /// </summary>
    /// <remarks>
    /// It has to be an absolute <c>http</c> or <c>https</c> URL without a query or fragment (they would be dropped). A trailing
    /// <c>/</c> is not needed and is ignored. A base path that is missing or wrong is answered by the engine with a
    /// "not found", which this package reports as <see cref="VisionEngineFailure.Rejected"/> — the same as an unknown scenario.
    /// </remarks>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// The longest one call may take, from sending the request to having read the whole answer. When it is exceeded the call
    /// ends with <see cref="VisionEngineFailure.Unavailable"/>. Must be positive and at most about 24 days (the most
    /// <see cref="HttpClient"/> accepts): "no limit" cannot be configured. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The largest image, in bytes, this package sends. A larger image is not sent at all: the call ends with
    /// <see cref="VisionEngineFailure.Rejected"/>. Must be positive. Defaults to 12 MiB.
    /// </summary>
    /// <remarks>
    /// The engine has an upload limit of its own (10 MiB unless its host changed it) and answers an image over it with a
    /// client error, which is <see cref="VisionEngineFailure.Rejected"/> as well. Set this to the engine's limit, or below it, so
    /// a large image is refused here without the cost of sending it.
    /// </remarks>
    public long MaxImageBytes { get; set; } = 12L * 1024 * 1024;

    // HttpClient.Timeout accepts nothing longer than int.MaxValue milliseconds.
    internal static readonly TimeSpan LongestTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// The address <c>HttpClient</c> resolves the relative path <c>inspect</c> against: <see cref="BaseUrl"/> with exactly one
    /// trailing slash. <c>HttpClient</c> combines a relative request with its base address only when the base path ends in
    /// <c>/</c>; without it the last segment of the base path is replaced, not extended.
    /// </summary>
    internal Uri BaseAddress()
        => new(BaseUrl!.Trim().TrimEnd('/') + "/", UriKind.Absolute);
}

/// <summary>
/// Refuses to start a host that turned the engine client on with nowhere to call, or with a limit that is no limit. Names the
/// setting that is wrong, never its value.
/// </summary>
/// <remarks>
/// A value outside the range is refused, not quietly replaced by a default: a limit that loosens itself when a setting is
/// mistyped is the failure the limits exist to prevent, and a host that is told at startup corrects it once.
/// </remarks>
internal sealed class IyuUVisionOptionsValidator : IValidateOptions<IyuUVisionOptions>
{
    public ValidateOptionsResult Validate(string? name, IyuUVisionOptions options)
    {
        List<string>? failures = null;

        // The scheme check is what refuses a path such as "/api/u-vision": on Linux that parses as an absolute file:// URI.
        if (!Uri.TryCreate(options.BaseUrl?.Trim(), UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            (failures ??= []).Add(
                $"{nameof(IyuUVisionOptions)}.{nameof(IyuUVisionOptions.BaseUrl)} must be an absolute http or https URL "
                + "that includes the engine's base path, or AddIyuUVision must not be called.");
        }
        else if (baseUri.Query.Length > 1 || baseUri.Fragment.Length > 1)
        {
            (failures ??= []).Add(
                $"{nameof(IyuUVisionOptions)}.{nameof(IyuUVisionOptions.BaseUrl)} must not contain a query or a fragment.");
        }

        if (options.Timeout <= TimeSpan.Zero || options.Timeout > IyuUVisionOptions.LongestTimeout)
        {
            (failures ??= []).Add(
                $"{nameof(IyuUVisionOptions)}.{nameof(IyuUVisionOptions.Timeout)} must be positive and no longer than about 24 days.");
        }

        if (options.MaxImageBytes <= 0)
            (failures ??= []).Add($"{nameof(IyuUVisionOptions)}.{nameof(IyuUVisionOptions.MaxImageBytes)} must be positive.");

        return failures is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
