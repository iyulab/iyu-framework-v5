namespace Iyu.Core.Attachments;

/// <summary>Operation a file-access token authorizes.</summary>
/// <remarks>
/// The two writes are different grants. <see cref="Upload"/> is the one for an attachment being stored: it can
/// put the object there once, so what the application recorded when the upload finished stays what is stored.
/// <see cref="Replace"/> is for a key whose content is meant to change — a template, an avatar — and says so.
/// </remarks>
public enum FileTokenOp
{
    /// <summary>
    /// Create the object at the token's key. Refused when the key already holds one — while the token lives it
    /// cannot be used to write the object a second time.
    /// </summary>
    Upload,

    /// <summary>Read the object, or ask whether it exists.</summary>
    Download,

    /// <summary>Delete the object.</summary>
    Delete,

    /// <summary>Write the object at the token's key, replacing whatever it holds; creates it when absent.</summary>
    Replace,
}
