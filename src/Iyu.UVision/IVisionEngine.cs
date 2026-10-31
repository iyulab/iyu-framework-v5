namespace Iyu.UVision;

/// <summary>
/// Asks a vision judgment engine to judge one image against a scenario and returns the engine's verdict, or the reason there is
/// none. Pluggable: a host swaps the implementation in DI (a fake in a test, another engine) without changing the calling code.
/// </summary>
public interface IVisionEngine
{
    /// <summary>
    /// Sends one image and reads the verdict.
    /// </summary>
    /// <param name="scenarioId">The engine's identifier of the scenario to judge the image against.</param>
    /// <param name="image">The image bytes. It is not copied before sending and not kept.</param>
    /// <param name="fileName">
    /// The image's file name. Its extension decides the content type the image is sent as (<c>.png</c>, <c>.jpg</c>/<c>.jpeg</c>,
    /// <c>.webp</c>, <c>.gif</c>, <c>.bmp</c>; anything else as <c>application/octet-stream</c>), and the name is sent along as
    /// the upload's file name.
    /// </param>
    /// <param name="deviceLabel">A label for the device that captured the image; not sent when <c>null</c> or blank.</param>
    /// <param name="cancellationToken">Cancelling it throws <see cref="OperationCanceledException"/>.</param>
    /// <remarks>
    /// A failure is a result, not an exception: <see cref="VisionEngineResult.Failure"/> says whether the engine cannot judge
    /// now (<see cref="VisionEngineFailure.Unavailable"/> — retrying may help), refused the request
    /// (<see cref="VisionEngineFailure.Rejected"/> — it will again) or answered in a shape that cannot be trusted
    /// (<see cref="VisionEngineFailure.InvalidResponse"/>). Only the caller's own cancellation throws; a time limit of this
    /// package's own is <see cref="VisionEngineFailure.Unavailable"/>, not cancellation. A caller that must not act on a missing
    /// verdict as if it were an OK treats every failure as "not judged".
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="scenarioId"/> or <paramref name="fileName"/> is null, empty or blank.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled by the caller.</exception>
    Task<VisionEngineResult> InspectAsync(
        string scenarioId,
        ReadOnlyMemory<byte> image,
        string fileName,
        string? deviceLabel,
        CancellationToken cancellationToken = default);
}
