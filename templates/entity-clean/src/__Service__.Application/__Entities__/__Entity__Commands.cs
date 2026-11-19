using {{Service}}.Domain.{{Entities}};
using Msg.Ddd;

namespace {{Service}}.Application.{{Entities}};

public sealed record Create{{Entity}}({{CommandParameters}}) : ICommand<Guid>;

/// <summary>Returns false when the {{Entity}} does not exist.</summary>
public sealed record Update{{Entity}}(Guid Id, {{CommandParameters}}) : ICommand<bool>;

/// <summary>Returns false when the {{Entity}} does not exist.</summary>
public sealed record Delete{{Entity}}(Guid Id) : ICommand<bool>;

public sealed class Create{{Entity}}Handler(I{{Entity}}Repository repository, IUnitOfWork unitOfWork)
    : ICommandHandler<Create{{Entity}}, Guid>
{
    public async Task<Guid> HandleAsync(Create{{Entity}} command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var entity = {{Entity}}.Create({{CommandArguments}});
        repository.Add(entity);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return entity.Id;
    }
}

public sealed class Update{{Entity}}Handler(I{{Entity}}Repository repository, IUnitOfWork unitOfWork)
    : ICommandHandler<Update{{Entity}}, bool>
{
    public async Task<bool> HandleAsync(Update{{Entity}} command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var entity = await repository.GetAsync(command.Id, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        entity.Update({{CommandArguments}});
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public sealed class Delete{{Entity}}Handler(I{{Entity}}Repository repository, IUnitOfWork unitOfWork)
    : ICommandHandler<Delete{{Entity}}, bool>
{
    public async Task<bool> HandleAsync(Delete{{Entity}} command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var entity = await repository.GetAsync(command.Id, cancellationToken);
        if (entity is null)
        {
            return false;
        }

        entity.Delete();
        repository.Remove(entity);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
