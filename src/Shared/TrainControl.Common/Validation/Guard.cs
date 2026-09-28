namespace TrainControl.Common.Validation;

/// <summary>
/// Placeholder argument-validation helpers.
/// </summary>
public static class Guard
{
    public static void NotNullOrWhiteSpace(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be null or whitespace.", paramName);
        }
    }
}
