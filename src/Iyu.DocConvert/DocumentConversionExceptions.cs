namespace Iyu.DocConvert;

/// <summary>
/// The converter could not be used just now: it could not be reached, did not answer in time, or answered that
/// it is failing or overloaded. Nothing is wrong with the document — the same call may succeed later.
/// </summary>
/// <remarks>
/// This is the failure a caller answers with "conversion is unavailable" rather than "something broke here",
/// and the one worth retrying. The cause — whatever the backend's transport raised — is the
/// <see cref="Exception.InnerException"/>; a caller does not need to know which backend to tell this apart.
/// A call the caller itself cancelled is not this: it ends in <see cref="OperationCanceledException"/>.
/// </remarks>
public sealed class DocumentConverterUnavailableException : Exception
{
    public DocumentConverterUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The converter received the document and could not convert it — a damaged file, content the format's
/// converter rejects. Sending the same bytes again gives the same answer.
/// </summary>
/// <remarks>
/// Distinct from <see cref="NotSupportedException"/>, which is thrown before anything is sent when the content
/// type is not one the implementation converts, and from <see cref="DocumentConverterUnavailableException"/>,
/// which says nothing about the document.
/// </remarks>
public sealed class DocumentConversionFailedException : Exception
{
    public DocumentConversionFailedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
