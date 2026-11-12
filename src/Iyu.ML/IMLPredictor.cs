namespace Iyu.ML;

/// <summary>
/// The port a caller asks a question through: a named model, a row of values, an answer with its stamp. What stands behind the
/// name is the implementation's business — a trained model, a rule, a statistic. A rule or a statistic is a legitimate
/// implementation, so an application can put a baseline and a model behind the same port and use whichever answers better.
/// </summary>
public interface IMLPredictor
{
    /// <summary>Answers for one row.</summary>
    /// <param name="model">The model's name.</param>
    /// <param name="row">Column name to value. A column the model does not use is ignored; the values an implementation reads are named in its own documentation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="MLModelNotFoundException">This implementation does not serve <paramref name="model"/>.</exception>
    /// <exception cref="ArgumentException">The row lacks what the model needs, or carries a value of a kind it cannot read.</exception>
    ValueTask<MLPrediction> PredictAsync(string model, IReadOnlyDictionary<string, object?> row, CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers for several rows, in order. The default asks <see cref="PredictAsync"/> once per row; an implementation with a
    /// cheaper batch path overrides it. One row the model cannot read fails the whole call.
    /// </summary>
    /// <param name="model">The model's name.</param>
    /// <param name="rows">The rows, each as in <see cref="PredictAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    async ValueTask<IReadOnlyList<MLPrediction>> PredictBatchAsync(
        string model, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var answers = new List<MLPrediction>(rows.Count);
        foreach (var row in rows)
            answers.Add(await PredictAsync(model, row, cancellationToken).ConfigureAwait(false));
        return answers;
    }
}

/// <summary>An <see cref="IMLPredictor"/> was asked for a model it does not serve.</summary>
public sealed class MLModelNotFoundException : InvalidOperationException
{
    /// <summary>Creates the exception for <paramref name="modelName"/>.</summary>
    public MLModelNotFoundException(string modelName)
        : base($"No model is served under the name '{modelName}'.")
    {
        ModelName = modelName;
    }

    /// <summary>The name that was asked for.</summary>
    public string ModelName { get; }
}
