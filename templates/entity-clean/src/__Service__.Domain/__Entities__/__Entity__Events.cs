using Msg.Ddd;

namespace {{Service}}.Domain.{{Entities}};

// Domain events: facts, in the past tense. Add the ones your business operations need
// (e.g. {{Entity}}Archived) and raise them from the aggregate's methods.

public sealed record {{Entity}}Created(Guid {{Entity}}Id) : DomainEvent;

public sealed record {{Entity}}Updated(Guid {{Entity}}Id) : DomainEvent;

public sealed record {{Entity}}Deleted(Guid {{Entity}}Id) : DomainEvent;
