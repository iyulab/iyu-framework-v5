namespace Iyu.Ml;

/// <summary>
/// The kinds of work a prediction answers. A prediction's stamp names one, so a caller can tell a label from a number from a
/// score without knowing which implementation produced it. The set is open: an implementation for a task not listed here
/// stamps its own name, and a caller compares names, not an enumeration.
/// </summary>
public static class MlTasks
{
    /// <summary>The answer is one of a fixed set of labels; the confidence, when given, is the share of the evidence behind it.</summary>
    public const string Classification = "classification";

    /// <summary>The answer is a number.</summary>
    public const string Regression = "regression";

    /// <summary>The answer is a score of how unusual the row is, larger meaning more unusual; there is no label.</summary>
    public const string Anomaly = "anomaly";
}
