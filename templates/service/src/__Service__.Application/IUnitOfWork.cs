namespace {{Service}}.Application;

/// <summary>Commits every change made through the repositories in one transaction.</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
