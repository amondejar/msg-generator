using Msg.Ddd;

namespace {{Service}}.Domain.{{Entities}};

public sealed partial class {{Entity}}
{
    /// <summary>
    /// {{Operation}}: the business rule is yours. Created once by Stratum and never overwritten; the command, its
    /// handler and POST /api/v1/{{entities-kebab}}/{id}/{{operation-kebab}} are generated from msg.json.
    /// </summary>
    public partial void {{Operation}}({{OperationParameters}})
    {
        // 1. Check the rule; a DomainException becomes a 422 with its message. For example:
        //    if (...) throw new DomainException("This {{Entity}} can't do {{Operation}} now.");
        // 2. Change the state (add properties in msg.json, or private fields here).

        UpdatedAt = DateTimeOffset.UtcNow;
        Raise(new {{OperationEvent}}(Id{{OperationEventArguments}}));
    }
}
