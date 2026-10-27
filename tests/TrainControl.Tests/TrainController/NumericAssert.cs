namespace TrainControl.Tests.TrainController;

/// <summary>
/// Explicit numeric comparisons with self-describing names. Used instead of
/// Assert.IsGreaterThan / IsLessThanOrEqualTo, whose parameter order changed across MSTest
/// versions — a swapped order would compile and silently invert the assertion.
/// </summary>
internal static class NumericAssert
{
    /// <summary>Fails unless <paramref name="value"/> &gt; 0.</summary>
    public static void Positive(double value, string message = "")
    {
        if (!(value > 0.0))
        {
            Assert.Fail($"Expected a value > 0 but was {value}. {message}");
        }
    }

    /// <summary>Fails unless <paramref name="value"/> is non-null and &lt;= <paramref name="upperBound"/>.</summary>
    public static void AtMost(double? value, double upperBound, string message = "")
    {
        if (value is not double actual || !(actual <= upperBound))
        {
            Assert.Fail($"Expected a value <= {upperBound} but was {value?.ToString() ?? "null"}. {message}");
        }
    }

    /// <summary>Fails unless <paramref name="value"/> is non-null and &gt; <paramref name="lowerBound"/>.</summary>
    public static void GreaterThan(double? value, double lowerBound, string message = "")
    {
        if (value is not double actual || !(actual > lowerBound))
        {
            Assert.Fail($"Expected a value > {lowerBound} but was {value?.ToString() ?? "null"}. {message}");
        }
    }
}
