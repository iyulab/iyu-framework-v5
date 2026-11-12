using Iyu.ML;

namespace Iyu.Tests.ML;

/// <summary>Rows and definitions shared by the ML tests.</summary>
internal static class MLTestData
{
    public static IReadOnlyDictionary<string, object?> Row(params (string Column, object? Value)[] cells) =>
        cells.ToDictionary(c => c.Column, c => c.Value);

    public static MLDatasetDefinition Definition(string name = "readings", params string[] features) =>
        new(name, (features.Length == 0 ? new[] { "value" } : features).Select(f => new MLColumn(f, MLColumnRole.Feature))
            .Append(new MLColumn("label", MLColumnRole.Label)));

    public static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> AsAsync(
        IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        foreach (var row in rows)
        {
            await Task.Yield();
            yield return row;
        }
    }
}
