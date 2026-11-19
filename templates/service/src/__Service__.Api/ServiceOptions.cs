using System.ComponentModel.DataAnnotations;

namespace {{Service}}.Api;

/// <summary>Example of strongly-typed, validated configuration ("Service" section).</summary>
public sealed class ServiceOptions
{
    public const string SectionName = "Service";

    [Required]
    public string Name { get; init; } = string.Empty;

    [Required, RegularExpression(@"^\d+\.\d+\.\d+([-+].*)?$", ErrorMessage = "Version must be SemVer (e.g. 1.2.3).")]
    public string Version { get; init; } = string.Empty;
}
