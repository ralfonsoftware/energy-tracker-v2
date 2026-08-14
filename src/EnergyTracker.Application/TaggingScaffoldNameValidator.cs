namespace EnergyTracker.Application;

/// <summary>
/// Shared Name validation for the nine Room/PowerPoint/Device create/rename use cases — a plain
/// helper, not a generic base class over the three entities (their parent-validation rules
/// differ enough that forcing them through one shared type would obscure that, not simplify it).
/// </summary>
public static class TaggingScaffoldNameValidator
{
    public const int MaxNameLength = 200;

    public static string Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new TaggingScaffoldValidationException("Name must not be blank.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw new TaggingScaffoldValidationException($"Name must not exceed {MaxNameLength} characters.");
        }

        return trimmed;
    }
}
