using Stratum.Core.Model;

namespace Stratum.Core.Generation;

/// <summary>Tokens of the replica templates (the class, its table and the handler that keeps it up to date).</summary>
public static class ReplicaCodeGen
{
    public static Dictionary<string, string> Tokens(ServiceDefinition definition, ReplicaDefinition replica)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(replica);
        var props = replica.Properties;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Publisher"] = replica.Service,
            ["Entity"] = replica.Entity,
            ["Replica"] = replica.Name,
            ["ReplicaPlural"] = replica.Plural,
            ["ReplicaTable"] = "replica_" + EntityCodeGen.Kebab(PropertyTypes.Pluralize(replica.Entity)).Replace('-', '_'),
            ["ReplicaProperties"] = string.Join("\n\n", props.Select(p => $"    public {PropertyTypes.EntityCSharp(p)} {p.Name} {{ get; set; }}{PropertyTypes.EntityInitializer(p)}")),
            ["ReplicaConfiguration"] = string.Join("\n", props.Select(Configuration).Where(l => l.Length > 0)),
            ["ReplicaCopy"] = string.Join("\n", props.Select(p => $"            replica.{p.Name} = integrationEvent.{p.Name};")),
            ["ReplicaFields"] = props.Count == 0 ? "only its Id" : string.Join(", ", props.Select(p => p.Name)),
        };
    }

    /// <summary>The DbSet of every replica (for the DbContext of the service).</summary>
    public static IEnumerable<string> DbSets(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Persistence is null ? [] : (definition.Replicas ?? []).Select(r =>
            $"    public DbSet<global::{ClassNamespace(definition, r)}.{r.Name}> {r.Plural} => Set<global::{ClassNamespace(definition, r)}.{r.Name}>();");
    }

    /// <summary>clean-ddd: the reader of every replica for the use cases (ICustomerReplicas → CustomerReplicaStore).</summary>
    public static IEnumerable<string> Registrations(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var service = definition.Service.Name;
        return definition.Persistence is null || definition.Architecture != Architectures.CleanDdd ? [] : (definition.Replicas ?? []).Select(r =>
            $"        services.AddScoped<global::{service}.Application.Replicas.{r.Service}.I{r.Plural}, global::{service}.Infrastructure.Persistence.Replicas.{r.Service}.{r.Name}Store>();");
    }

    private static string ClassNamespace(ServiceDefinition definition, ReplicaDefinition replica) =>
        definition.Architecture == Architectures.CleanDdd
            ? $"{definition.Service.Name}.Application.Replicas.{replica.Service}"
            : $"{definition.Service.Name}.Api.Replicas.{replica.Service}";

    private static string Configuration(PropertyDefinition p) => p.Type switch
    {
        "string" => $"        builder.Property(e => e.{p.Name}).HasMaxLength({ValueRules.MaxLength(p)}){(p.Nullable ? string.Empty : ".IsRequired()")};",
        "decimal" => $"        builder.Property(e => e.{p.Name}).HasPrecision(18, 2);",
        _ => string.Empty,
    };
}
