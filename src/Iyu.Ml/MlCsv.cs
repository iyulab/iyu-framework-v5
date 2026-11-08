using System.Globalization;
using System.Text;

namespace Iyu.Ml;

/// <summary>
/// Writes a dataset as CSV: the default exchange format, because it needs nothing beyond the platform. The text does not depend
/// on the machine's culture — a number written under one regional setting must read back under any other — and follows RFC 4180:
/// a field holding a comma, a quote or a line break is quoted, a quote inside it doubled, and records end with CR LF.
/// </summary>
/// <remarks>
/// The file is data for a training engine, so values are written exactly as they are: a text that begins with <c>=</c>, <c>+</c>,
/// <c>-</c> or <c>@</c> is not altered. A spreadsheet application treats such a cell as a formula, which makes the file unsafe to
/// open in one when any text column can hold what a user typed. Do not hand an export to a person to open in a spreadsheet;
/// neutralising the leading character would change the values a model learns from.
/// </remarks>
public static class MlCsv
{
    private const string LineEnd = "\r\n";

    private static readonly System.Buffers.SearchValues<char> NeedsQuoting = System.Buffers.SearchValues.Create(",\"\r\n");

    /// <summary>
    /// Writes the header (the definition's column names, in order) and then one record per row, taking each value from the row
    /// under the column's name. A missing or <c>null</c> value is written as an empty field, so a null and an empty string read
    /// back the same.
    /// </summary>
    /// <param name="definition">Which columns, in which order.</param>
    /// <param name="rows">The rows, as <see cref="IMlDatasetSource.ReadAsync"/> returns them.</param>
    /// <param name="writer">Where the text goes; it is not disposed.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The number of rows written.</returns>
    public static async ValueTask<long> WriteAsync(
        MlDatasetDefinition definition,
        IAsyncEnumerable<IReadOnlyDictionary<string, object?>> rows,
        TextWriter writer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(writer);

        await writer.WriteAsync(Join(definition.Columns.Select(c => Escape(c.Name))) + LineEnd).ConfigureAwait(false);

        long count = 0;
        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // A source need not watch the token itself; a cancelled export stops at the next row either way.
            cancellationToken.ThrowIfCancellationRequested();
            var fields = definition.Columns.Select(c => row.TryGetValue(c.Name, out var value) ? Escape(Format(value)) : string.Empty);
            var record = Join(fields);
            // A record of one empty field is indistinguishable from a blank line, which many readers skip: quote it.
            if (definition.Columns.Count == 1 && record.Length == 0) record = "\"\"";
            await writer.WriteAsync(record + LineEnd).ConfigureAwait(false);
            count++;
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    /// <summary>
    /// A value as text, the same under every culture: numbers with a point and no grouping, dates and times in ISO 8601,
    /// booleans as <c>true</c> / <c>false</c>, an enumeration by name, bytes as base 64, <c>null</c> as the empty string. A value that has no
    /// text of its own is refused.
    /// </summary>
    /// <param name="value">The value.</param>
    public static string Format(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        bool flag => flag ? "true" : "false",
        DateTime moment => moment.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset moment => moment.ToString("O", CultureInfo.InvariantCulture),
        DateOnly day => day.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),
        Guid id => id.ToString("D", CultureInfo.InvariantCulture),
        Enum member => member.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        _ => Text(value),
    };

    // A value with no text of its own prints its type name, which would land in the file as if it were data.
    private static string Text(object value)
    {
        var text = value.ToString() ?? string.Empty;
        if (text == value.GetType().ToString())
            throw new ArgumentException($"A value of type {value.GetType().Name} has no text form to write to a CSV field.", nameof(value));
        return text;
    }

    /// <summary>A field ready to sit in a record: quoted, with inner quotes doubled, when it holds a comma, a quote or a line break.</summary>
    /// <param name="field">The field's text.</param>
    public static string Escape(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.AsSpan().IndexOfAny(NeedsQuoting) < 0)
            return field;
        return new StringBuilder(field.Length + 2).Append('"').Append(field.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"').ToString();
    }

    private static string Join(IEnumerable<string> fields) => string.Join(',', fields);
}
