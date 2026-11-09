namespace Iyu.VaultAi.FolderChatbots;

/// <summary>
/// A first judgement of which files are worth uploading, by extension. The list is the one vault-ai's parsers accept, plus the
/// common plain-text extensions it takes by looking at the content. The server has the last word: a file that passes here and is
/// refused with 415 is counted as skipped.
/// </summary>
internal static class FolderChatbotSupportedFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Documents (Office, PDF, Hangul)
        "pdf", "doc", "docx", "dot", "dotx", "xls", "xlsx", "xlsm", "ppt", "pptx", "pps", "ppsx",
        "odt", "ods", "odp", "rtf", "vsdx", "hwp", "hwpx",
        // Web, e-books
        "html", "htm", "epub",
        // Tabular text
        "csv", "tsv",
        // Images
        "png", "jpg", "jpeg", "gif", "webp", "bmp", "tif", "tiff", "heic", "heif", "svg",
        // Drawings
        "dwg", "dxf",
        // Audio
        "mp3", "wav", "m4a", "flac", "ogg", "aac", "wma",
        // Video
        "mp4", "mov", "avi", "mkv", "webm", "wmv",
        // Text without a parser (the server decides from the content)
        "txt", "md", "markdown", "json", "xml", "yaml", "yml", "log", "ini", "cfg", "conf", "sql",
    };

    /// <summary>Whether the extension of <paramref name="fileName"/> (a relative path will do) is one to upload.</summary>
    public static bool IsSupported(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Length > 1 && Extensions.Contains(ext[1..]);
    }
}
