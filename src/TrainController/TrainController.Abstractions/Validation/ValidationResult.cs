namespace TrainController.Abstractions.Validation;

/// <summary>Outcome of validating an input or output. Never throws for bad data.</summary>
public sealed class ValidationResult
{
    private ValidationResult(IReadOnlyList<string> errors) => Errors = errors;

    public IReadOnlyList<string> Errors { get; }

    public bool IsValid => Errors.Count == 0;

    public static ValidationResult Valid { get; } = new ValidationResult(Array.Empty<string>());

    public static ValidationResult From(IReadOnlyList<string> errors) =>
        errors.Count == 0 ? Valid : new ValidationResult(errors);

    public override string ToString() => IsValid ? "valid" : string.Join("; ", Errors);
}
