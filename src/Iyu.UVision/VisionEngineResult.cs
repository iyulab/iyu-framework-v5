namespace Iyu.UVision;

/// <summary>
/// The verdict the engine gave for one image, as the engine gave it. This package does not correct, soften or reinterpret it.
/// </summary>
/// <param name="Verdict">Exactly <c>OK</c> or <c>NG</c>; any other value is an <see cref="VisionEngineFailure.InvalidResponse"/>, not a verdict.</param>
/// <param name="Confidence">The engine's own confidence for the verdict; <c>null</c> when the engine reported none.</param>
/// <param name="ImageId">The engine's identifier for the judged image, to look the record up in the engine later.</param>
/// <param name="RequiresReview">
/// Whether the engine recommends that a person look at the image. It is advice and never changes <paramref name="Verdict"/>;
/// <c>false</c> when the engine did not say.
/// </param>
/// <param name="Findings">
/// The engine's explanation. A list of findings is joined with <c>"; "</c>. <c>null</c> when the engine gave none (an empty
/// text counts as none).
/// </param>
public sealed record VisionVerdict(string Verdict, decimal? Confidence, string ImageId, bool RequiresReview, string? Findings);

/// <summary>Why a call ended without a verdict.</summary>
public enum VisionEngineFailure
{
    /// <summary>
    /// The engine could not give a verdict now: it could not be reached, did not answer in time, answered that it cannot
    /// judge (HTTP 503, which the engine uses to refuse a verdict rather than guess one), was overloaded or failed. The same
    /// request may succeed later. No verdict is made up.
    /// </summary>
    Unavailable,

    /// <summary>
    /// The request itself was refused and the same request will be refused again: the image is over
    /// <see cref="IyuUVisionOptions.MaxImageBytes"/> (nothing was sent), or the engine answered with a client error — an
    /// unknown scenario, an unsupported or empty image, an image over its own limit, or a wrong <see cref="IyuUVisionOptions.BaseUrl"/>.
    /// </summary>
    Rejected,

    /// <summary>The engine answered success but the answer is not the shape its contract promises, so there is no verdict to trust.</summary>
    InvalidResponse,
}

/// <summary>The outcome of one call: a verdict, or the reason there is none.</summary>
/// <param name="Verdict">The engine's verdict; <c>null</c> exactly when <paramref name="Failure"/> is set.</param>
/// <param name="Failure">Why there is no verdict; <c>null</c> when there is one.</param>
/// <param name="Message">A sentence for a log or a person on a failure (it never carries the image); <c>null</c> on success.</param>
public sealed record VisionEngineResult(VisionVerdict? Verdict, VisionEngineFailure? Failure, string? Message)
{
    /// <summary>Whether the engine gave a verdict.</summary>
    public bool Ok => Verdict is not null;

    internal static VisionEngineResult Success(VisionVerdict verdict) => new(verdict, null, null);

    internal static VisionEngineResult Fail(VisionEngineFailure failure, string message) => new(null, failure, message);
}
