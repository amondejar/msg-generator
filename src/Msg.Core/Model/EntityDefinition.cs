namespace Msg.Core.Model;

/// <summary>Database used by the service. Set the first time an entity is added.</summary>
public sealed record PersistenceSettings
{
    public const string Postgres = "postgres";
    public const string SqlServer = "sqlserver";
    public static IReadOnlyList<string> Providers { get; } = [Postgres, SqlServer];

    public required string Provider { get; init; }
}

public sealed record EntityDefinition
{
    /// <summary>PascalCase singular, e.g. <c>Product</c>.</summary>
    public required string Name { get; init; }

    /// <summary>PascalCase plural, e.g. <c>Products</c>. Used for the folder, DbSet and route.</summary>
    public required string Plural { get; init; }

    public required IReadOnlyList<PropertyDefinition> Properties { get; init; }
}

public sealed record PropertyDefinition
{
    public required string Name { get; init; }

    /// <summary>One of <see cref="PropertyTypes.All"/> (msg names, not C# names).</summary>
    public required string Type { get; init; }

    public bool Nullable { get; init; }
}
