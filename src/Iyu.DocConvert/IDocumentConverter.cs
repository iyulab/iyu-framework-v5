namespace Iyu.DocConvert;

/// <summary>Converts an Office/OpenDocument file to PDF. Pluggable backend — a host swaps the
/// implementation via DI without changing any calling code.</summary>
public interface IDocumentConverter
{
    /// <summary>
    /// Converts <paramref name="source"/> to PDF and returns the PDF bytes as a new, independent,
    /// seekable stream positioned at 0. <paramref name="source"/> is read to completion but not
    /// disposed — same convention as <c>IAttachmentStorage.SaveAsync</c> (Iyu.Core): the caller
    /// owns it and remains responsible for disposal.
    /// </summary>
    /// <param name="source">The source document's bytes.</param>
    /// <param name="sourceContentType">
    /// The source document's MIME type (e.g. <c>application/vnd.openxmlformats-officedocument.
    /// wordprocessingml.document</c> for <c>.docx</c>). Implementations use this to tell the
    /// backend how to interpret the bytes — an unrecognized type is a caller error, not a
    /// silent best-effort guess.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="NotSupportedException"><paramref name="sourceContentType"/> is not a
    /// type this implementation knows how to convert.</exception>
    Task<Stream> ConvertToPdfAsync(Stream source, string sourceContentType, CancellationToken ct = default);
}
