namespace {{Service}}.Domain.{{Entities}};

/// <summary>
/// Your part of the <see cref="{{Entity}}"/> aggregate. Created once by Stratum and never overwritten.
/// Properties, Create, Update and Delete are generated in {{Entity}}.g.cs from stratum.json: change the
/// properties in the Studio or with "stratum entity update". Here go your rules and operations, e.g.:
/// <code>
/// partial void EnsureInvariants()
/// {
///     if (Price &lt; 0) throw new DomainException("Price can't be negative.");
/// }
///
/// public void Archive()
/// {
///     // change state...
///     Raise(new {{Entity}}Archived(Id));   // declare the event in {{Entity}}Events.cs
/// }
/// </code>
/// </summary>
public sealed partial class {{Entity}}
{
}
