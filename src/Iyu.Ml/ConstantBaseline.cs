namespace Iyu.Ml;

/// <summary>
/// The answer that needs no model: always the label seen most often (classification) or the median of the labels (regression).
/// Any model that cannot beat it has learned nothing, so it is the line every comparison starts from. It ignores the row it is
/// given.
/// </summary>
/// <remarks>
/// It is fitted once from the label column of a dataset and then answers from memory. Rows with no label are left out of the fit.
/// For classification the confidence is the share of the fitted rows that carried the answer, which is exactly how often always
/// saying it was right on that data.
/// </remarks>
public sealed class ConstantBaseline : IMlPredictor
{
    private ConstantBaseline(string modelName, string version, string task, object? value, double? confidence, long rows)
    {
        ModelName = modelName;
        Version = version;
        Task = task;
        Value = value;
        Confidence = confidence;
        Rows = rows;
    }

    /// <summary>The name this baseline answers to.</summary>
    public string ModelName { get; }

    /// <summary>The version it stamps on its answers.</summary>
    public string Version { get; }

    /// <summary>Either <see cref="MlTasks.Classification"/> or <see cref="MlTasks.Regression"/>.</summary>
    public string Task { get; }

    /// <summary>The answer it always gives.</summary>
    public object? Value { get; }

    /// <summary>For classification, the share of the fitted rows carrying <see cref="Value"/>; for regression, <c>null</c>.</summary>
    public double? Confidence { get; }

    /// <summary>How many rows with a label the fit used.</summary>
    public long Rows { get; }

    /// <summary>Fits the most frequent label. Ties go to the label that sorts first, so the same data always gives the same answer.</summary>
    /// <param name="modelName">The name it will answer to.</param>
    /// <param name="version">The version it will stamp.</param>
    /// <param name="definition">The dataset's definition; its label column is read.</param>
    /// <param name="rows">The rows to fit from.</param>
    /// <exception cref="ArgumentException">The dataset has no label column, or a name is empty.</exception>
    /// <exception cref="InvalidOperationException">No row carries a label.</exception>
    public static ConstantBaseline FitClassification(
        string modelName, string version, MlDatasetDefinition definition, IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        var (label, counter) = (LabelOf(definition), new ClassCounter());
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
            counter.Add(row, label);
        return counter.Build(modelName, version);
    }

    /// <summary>The asynchronous <see cref="FitClassification"/>.</summary>
    /// <param name="modelName">The name it will answer to.</param>
    /// <param name="version">The version it will stamp.</param>
    /// <param name="definition">The dataset's definition; its label column is read.</param>
    /// <param name="rows">The rows to fit from.</param>
    /// <param name="cancellationToken">Cancels the fit.</param>
    public static async ValueTask<ConstantBaseline> FitClassificationAsync(
        string modelName, string version, MlDatasetDefinition definition,
        IAsyncEnumerable<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default)
    {
        var (label, counter) = (LabelOf(definition), new ClassCounter());
        ArgumentNullException.ThrowIfNull(rows);
        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            counter.Add(row, label);
        return counter.Build(modelName, version);
    }

    /// <summary>Fits the median of the label. The label values must be numbers.</summary>
    /// <param name="modelName">The name it will answer to.</param>
    /// <param name="version">The version it will stamp.</param>
    /// <param name="definition">The dataset's definition; its label column is read.</param>
    /// <param name="rows">The rows to fit from.</param>
    /// <exception cref="ArgumentException">The dataset has no label column, a name is empty, or a label is not a number.</exception>
    /// <exception cref="InvalidOperationException">No row carries a label.</exception>
    public static ConstantBaseline FitRegression(
        string modelName, string version, MlDatasetDefinition definition, IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        var (label, values) = (LabelOf(definition), new List<double>());
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
            AddNumber(values, row, label);
        return BuildRegression(modelName, version, values);
    }

    /// <summary>The asynchronous <see cref="FitRegression"/>.</summary>
    /// <param name="modelName">The name it will answer to.</param>
    /// <param name="version">The version it will stamp.</param>
    /// <param name="definition">The dataset's definition; its label column is read.</param>
    /// <param name="rows">The rows to fit from.</param>
    /// <param name="cancellationToken">Cancels the fit.</param>
    public static async ValueTask<ConstantBaseline> FitRegressionAsync(
        string modelName, string version, MlDatasetDefinition definition,
        IAsyncEnumerable<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default)
    {
        var (label, values) = (LabelOf(definition), new List<double>());
        ArgumentNullException.ThrowIfNull(rows);
        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            AddNumber(values, row, label);
        return BuildRegression(modelName, version, values);
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

        var stamp = new MlStamp(ModelName, Version, Task, MlValues.NewPredictionId());
        return new MlPrediction(Value, stamp, Confidence);
    }

    private static string LabelOf(MlDatasetDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Label
            ?? throw new ArgumentException($"Dataset '{definition.Name}' has no label column, so there is nothing to answer with.", nameof(definition));
    }

    private static void AddNumber(List<double> values, IReadOnlyDictionary<string, object?> row, string label)
    {
        if (row.TryGetValue(label, out var value) && MlValues.TryRead(value, label, out var number))
            values.Add(number);
    }

    private static ConstantBaseline BuildRegression(string modelName, string version, List<double> values)
    {
        RequireNames(modelName, version);
        if (values.Count == 0)
            throw new InvalidOperationException("No row carries a label, so there is nothing to take the median of.");
        values.Sort();
        return new ConstantBaseline(modelName, version, MlTasks.Regression, MlValues.Median(values), null, values.Count);
    }

    private static void RequireNames(string modelName, string version)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentException("A baseline needs a model name.", nameof(modelName));
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("A baseline needs a version.", nameof(version));
    }

    private sealed class ClassCounter
    {
        private readonly Dictionary<string, (object? Sample, long Count)> _counts = new(StringComparer.Ordinal);
        private long _total;

        public void Add(IReadOnlyDictionary<string, object?> row, string label)
        {
            if (!row.TryGetValue(label, out var value) || value is null)
                return;
            var key = MlCsv.Format(value);
            _counts[key] = _counts.TryGetValue(key, out var seen) ? (seen.Sample, seen.Count + 1) : (value, 1);
            _total++;
        }

        public ConstantBaseline Build(string modelName, string version)
        {
            RequireNames(modelName, version);
            if (_total == 0)
                throw new InvalidOperationException("No row carries a label, so there is nothing to answer with.");

            // Most frequent; on a tie the label that sorts first, so the answer does not depend on the order rows arrived in.
            var best = _counts.OrderByDescending(p => p.Value.Count).ThenBy(p => p.Key, StringComparer.Ordinal).First();
            return new ConstantBaseline(modelName, version, MlTasks.Classification, best.Value.Sample, (double)best.Value.Count / _total, _total);
        }
    }
}
