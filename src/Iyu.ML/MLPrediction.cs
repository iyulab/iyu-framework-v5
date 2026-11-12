namespace Iyu.ML;

/// <summary>
/// Where a prediction came from, in the framework's own terms: enough to tell a later reader which model answered, and to
/// attach the true value to it afterwards. An implementation maps whatever its engine records into this; the engine's own
/// vocabulary is not carried here.
/// </summary>
public sealed record MLStamp
{
    /// <summary>Creates a stamp.</summary>
    /// <param name="modelName">The name the caller asked for.</param>
    /// <param name="version">Which version of that model answered; any text that tells one version from another.</param>
    /// <param name="task">What the answer is — see <see cref="MLTasks"/>.</param>
    /// <param name="predictionId">Unique per answer; the handle a later true value is attached to.</param>
    /// <param name="notes">Free-form annotations the implementation wants kept with the answer, or <c>null</c>.</param>
    public MLStamp(string modelName, string version, string task, string predictionId, IReadOnlyDictionary<string, string>? notes = null)
    {
        ModelName = Require(modelName, nameof(modelName));
        Version = Require(version, nameof(version));
        Task = Require(task, nameof(task));
        PredictionId = Require(predictionId, nameof(predictionId));
        Notes = notes;
    }

    /// <summary>The name the caller asked for.</summary>
    public string ModelName { get; }

    /// <summary>Which version of the model answered.</summary>
    public string Version { get; }

    /// <summary>What the answer is — see <see cref="MLTasks"/>.</summary>
    public string Task { get; }

    /// <summary>Unique per answer.</summary>
    public string PredictionId { get; }

    /// <summary>Free-form annotations kept with the answer, or <c>null</c>.</summary>
    public IReadOnlyDictionary<string, string>? Notes { get; }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A stamp field cannot be empty.", name) : value;
}

/// <summary>One answer: the value, how sure the implementation is when it can say, and where it came from.</summary>
public sealed record MLPrediction
{
    /// <summary>Creates a prediction.</summary>
    /// <param name="value">A label, a number or a score, according to <see cref="MLStamp.Task"/>.</param>
    /// <param name="stamp">Where the answer came from.</param>
    /// <param name="confidence">From 0 to 1, or <c>null</c> when the implementation has no honest figure. Never a stand-in such as 1.</param>
    public MLPrediction(object? value, MLStamp stamp, double? confidence = null)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        if (confidence is { } c && !(c >= 0 && c <= 1))
            throw new ArgumentOutOfRangeException(nameof(confidence), "A confidence is between 0 and 1, or absent.");
        Value = value;
        Stamp = stamp;
        Confidence = confidence;
    }

    /// <summary>The answer.</summary>
    public object? Value { get; }

    /// <summary>From 0 to 1, or <c>null</c> when the implementation has no honest figure.</summary>
    public double? Confidence { get; }

    /// <summary>Where the answer came from.</summary>
    public MLStamp Stamp { get; }
}
