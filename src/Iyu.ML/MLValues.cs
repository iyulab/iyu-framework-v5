using System.Globalization;

namespace Iyu.ML;

/// <summary>Reading numbers out of the loosely typed values a row carries.</summary>
internal static class MLValues
{
    /// <summary>
    /// Reads <paramref name="value"/> as a number. <c>null</c> is "no value" and returns <c>false</c>; a numeric value returns
    /// <c>true</c>; anything else — text, a boolean, a non-finite number — is an error naming <paramref name="column"/>, because
    /// guessing at what a value was meant to be would turn a data mistake into a quietly wrong answer.
    /// </summary>
    public static bool TryRead(object? value, string column, out double number)
    {
        number = 0;
        if (value is null)
            return false;

        switch (value)
        {
            case double or float or decimal or sbyte or byte or short or ushort or int or uint or long or ulong:
                number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                break;
            default:
                throw new ArgumentException($"Column '{column}' holds a {value.GetType().Name}, which is not a number.", "row");
        }

        if (!double.IsFinite(number))
            throw new ArgumentException($"Column '{column}' holds a number that is not finite.", "row");
        return true;
    }

    /// <summary>The median of <paramref name="sorted"/>, which must be sorted ascending and not empty.</summary>
    public static double Median(IReadOnlyList<double> sorted)
    {
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>A fresh identifier for one answer; time-ordered, so answers sort in the order they were given.</summary>
    public static string NewPredictionId() => Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
}
