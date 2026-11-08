namespace Iyu.Ml;

/// <summary>One feature of a <see cref="RobustZBaseline"/>: the typical value and the spread a deviation is measured in.</summary>
/// <param name="Column">The feature's column name.</param>
/// <param name="Center">The median of the fitted values.</param>
/// <param name="Scale">The spread, in the same unit as the values, comparable to a standard deviation.</param>
public sealed record RobustZFeature(string Column, double Center, double Scale);

/// <summary>
/// An anomaly score from a rule that needs no training run: how many robust standard deviations a value lies from the typical
/// one, taken over every feature, the largest wins. "Typical" is the median and "spread" the median absolute deviation scaled to
/// a standard deviation, so a handful of extreme rows in the fitted data do not move either — unlike a mean and a standard
/// deviation, which the very outliers being looked for would drag along.
/// </summary>
/// <remarks>
/// <para>
/// The score is <c>max over features of |value - median| / scale</c>; larger is more unusual and 0 is exactly typical. A feature
/// whose median absolute deviation is zero (more than half the fitted values are equal) takes its scale from the mean absolute
/// deviation instead; one with no spread at all carries no signal and is left out of the score. The row may omit a feature or
/// give it <c>null</c>, which contributes nothing; a row that offers no scored feature at all is refused.
/// </para>
/// <para>
/// Every feature column of the dataset is used and must hold numbers.
/// </para>
/// </remarks>
public sealed class RobustZBaseline : IMlPredictor
{
    // Make the median absolute deviation estimate a standard deviation under a normal distribution (1 / the 75th percentile of N(0,1)).
    private const double MadToSigma = 1.4826;

    // The same for the mean absolute deviation (sqrt(pi / 2)).
    private const double MeanAbsoluteDeviationToSigma = 1.2533;

    private readonly RobustZFeature[] _features;

    private RobustZBaseline(string modelName, string version, RobustZFeature[] features, long rows)
    {
        ModelName = modelName;
        Version = version;
        _features = features;
        Rows = rows;
    }

    /// <summary>The name this baseline answers to.</summary>
    public string ModelName { get; }

    /// <summary>The version it stamps on its answers.</summary>
    public string Version { get; }

    /// <summary>The features that carry a signal, with what was fitted for each.</summary>
    public IReadOnlyList<RobustZFeature> Features => _features;

    /// <summary>How many rows the fit read.</summary>
    public long Rows { get; }

    /// <summary>Fits the median and spread of every feature column.</summary>
    /// <param name="modelName">The name it will answer to.</param>
    /// <param name="version">The version it will stamp.</param>
    /// <param name="definition">The dataset's definition; its feature columns are read.</param>
    /// <param name="rows">The rows to fit from. A <c>null</c> or missing value is skipped.</param>
    /// <exception cref="ArgumentException">A name is empty, or a value is not a finite number.</exception>
    /// <exception cref="InvalidOperationException">A feature has no values, or none of them has any spread.</exception>
    public static RobustZBaseline Fit(
        string modelName, string version, MlDatasetDefinition definition, IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(rows);
        var columns = definition.Features.ToArray();
        var collected = columns.Select(_ => new List<double>()).ToArray();
        long count = 0;
        foreach (var row in rows)
        {
            Collect(row, columns, collected);
            count++;
        }
        return Build(modelName, version, columns, collected, count);
    }

    /// <summary>The asynchronous <see cref="Fit"/>.</summary>
    /// <param name="modelName">The name it will answer to.</param>
    /// <param name="version">The version it will stamp.</param>
    /// <param name="definition">The dataset's definition; its feature columns are read.</param>
    /// <param name="rows">The rows to fit from.</param>
    /// <param name="cancellationToken">Cancels the fit.</param>
    public static async ValueTask<RobustZBaseline> FitAsync(
        string modelName, string version, MlDatasetDefinition definition,
        IAsyncEnumerable<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(rows);
        var columns = definition.Features.ToArray();
        var collected = columns.Select(_ => new List<double>()).ToArray();
        long count = 0;
        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            Collect(row, columns, collected);
            count++;
        }
        return Build(modelName, version, columns, collected, count);
    }

    /// <inheritdoc />
    public ValueTask<MlPrediction> PredictAsync(string model, IReadOnlyDictionary<string, object?> row, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Predict(model, row));

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<MlPrediction>> PredictBatchAsync(
        string model, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var answers = new MlPrediction[rows.Count];
        for (var i = 0; i < answers.Length; i++)
            answers[i] = Predict(model, rows[i]);
        return ValueTask.FromResult<IReadOnlyList<MlPrediction>>(answers);
    }

    private MlPrediction Predict(string model, IReadOnlyDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(row);
        if (!string.Equals(model, ModelName, StringComparison.Ordinal))
            throw new MlModelNotFoundException(model);

        double? score = null;
        foreach (var feature in _features)
        {
            if (!row.TryGetValue(feature.Column, out var value) || !MlValues.TryRead(value, feature.Column, out var number))
                continue;
            var z = Math.Abs(number - feature.Center) / feature.Scale;
            score = score is { } best && best >= z ? best : z;
        }

        if (score is null)
            throw new ArgumentException("The row holds none of the columns this model scores: " + string.Join(", ", _features.Select(f => f.Column)) + ".", nameof(row));

        var stamp = new MlStamp(ModelName, Version, MlTasks.Anomaly, MlValues.NewPredictionId());
        return new MlPrediction(score.Value, stamp);
    }

    private static void Collect(IReadOnlyDictionary<string, object?> row, string[] columns, List<double>[] collected)
    {
        for (var i = 0; i < columns.Length; i++)
            if (row.TryGetValue(columns[i], out var value) && MlValues.TryRead(value, columns[i], out var number))
                collected[i].Add(number);
    }

    private static RobustZBaseline Build(string modelName, string version, string[] columns, List<double>[] collected, long rows)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("A baseline needs a model name.", nameof(modelName));
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("A baseline needs a version.", nameof(version));

        var features = new List<RobustZFeature>();
        for (var i = 0; i < columns.Length; i++)
        {
            var values = collected[i];
            if (values.Count == 0)
                throw new InvalidOperationException($"Column '{columns[i]}' has no values, so there is nothing to measure a deviation from.");

            values.Sort();
            var center = MlValues.Median(values);
            var deviations = values.Select(v => Math.Abs(v - center)).ToList();
            deviations.Sort();
            var mad = MlValues.Median(deviations);
            var scale = mad > 0
                ? MadToSigma * mad
                : MeanAbsoluteDeviationToSigma * deviations.Average();
            if (scale > 0)
                features.Add(new RobustZFeature(columns[i], center, scale));
        }

        if (features.Count == 0)
            throw new InvalidOperationException("No feature varies in the fitted rows, so no value would stand out.");
        return new RobustZBaseline(modelName, version, features.ToArray(), rows);
    }
}
