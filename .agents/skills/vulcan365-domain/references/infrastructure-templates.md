# Infrastructure Templates

## DbContext Template

```csharp
using Microsoft.EntityFrameworkCore;
using {Namespace}.Infrastructure.Common;

namespace {Namespace}.Infrastructure.Persistence;

public class {Name}DbContext : DbContext
{
    public {Name}DbContext(DbContextOptions<{Name}DbContext> options)
        : base(options)
    {
        // NoTracking by default for read performance.
        // Command handlers must use .AsTracking() or DbSet.Add()/Remove() explicitly.
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    // Add DbSet<T> for each aggregate and queryable child entity
    // public DbSet<Account> Accounts => Set<Account>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("{schema}");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof({Name}DbContext).Assembly);
    }

    public IReadOnlyCollection<IDomainEvent> CollectDomainEvents()
    {
        // CRITICAL: Disable auto-detect to prevent change tracking interference
        var autoDetect = ChangeTracker.AutoDetectChangesEnabled;
        try
        {
            ChangeTracker.AutoDetectChangesEnabled = false;

            var domainEntities = ChangeTracker
                .Entries<IHasDomainEvents>()
                .Where(e => e.Entity.DomainEvents.Any())
                .ToList();

            var domainEvents = domainEntities
                .SelectMany(e => e.Entity.DomainEvents)
                .ToList();

            domainEntities.ForEach(e => e.Entity.ClearDomainEvents());

            return domainEvents;
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = autoDetect;
        }
    }
}
```

## EfUnitOfWork Template

```csharp
using {Namespace}.Infrastructure.Abstractions;

namespace {Namespace}.Infrastructure.Persistence;

public sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly {Name}DbContext _dbContext;
    private readonly IMediator _mediator;
    private readonly IEventPublisher _eventPublisher;

    public EfUnitOfWork(
        {Name}DbContext dbContext,
        IMediator mediator,
        IEventPublisher eventPublisher)
    {
        _dbContext = dbContext;
        _mediator = mediator;
        _eventPublisher = eventPublisher;
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        // Collect events BEFORE save (while entities are tracked)
        var domainEvents = _dbContext.CollectDomainEvents();

        // Save changes
        var result = await _dbContext.SaveChangesAsync(ct);

        // Dispatch events AFTER successful save
        foreach (var domainEvent in domainEvents)
        {
            await _mediator.PublishDomainEventAsync(domainEvent, ct);
        }

        if (domainEvents.Count > 0)
        {
            await _eventPublisher.PublishAsync(domainEvents, ct);
        }

        return result;
    }
}
```

## EfRepository Template

```csharp
using Microsoft.EntityFrameworkCore;
using {Namespace}.Infrastructure.Abstractions;
using {Namespace}.Infrastructure.Common;

namespace {Namespace}.Infrastructure.Persistence;

public sealed class EfRepository<TAggregate> : IRepository<TAggregate>
    where TAggregate : BaseEntity, IAggregateRoot
{
    private readonly {Name}DbContext _dbContext;
    private readonly DbSet<TAggregate> _dbSet;

    public EfRepository({Name}DbContext dbContext)
    {
        _dbContext = dbContext;
        _dbSet = dbContext.Set<TAggregate>();
    }

    public async Task<TAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _dbSet.AsTracking().FirstOrDefaultAsync(e => e.Id == id, ct);

    public async Task<IReadOnlyList<TAggregate>> ListAsync(CancellationToken ct = default)
        => await _dbSet.AsNoTracking().ToListAsync(ct);

    public async Task AddAsync(TAggregate entity, CancellationToken ct = default)
        => await _dbSet.AddAsync(entity, ct);

    public void Update(TAggregate entity)
        => _dbSet.Update(entity);

    public void Remove(TAggregate entity)
        => _dbSet.Remove(entity);
}
```

**Note:** `GetByIdAsync` uses `.AsTracking()` because it's typically called from command handlers that will mutate the entity. `ListAsync` uses `.AsNoTracking()` for read performance.

## DesignTimeDbContextFactory Template

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace {Namespace}.Infrastructure.Persistence;

public class {Name}DbContextFactory : IDesignTimeDbContextFactory<{Name}DbContext>
{
    public {Name}DbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<{Name}DbContext>();
        optionsBuilder.UseSqlServer(
            "Server=localhost;Database={dbname};User Id={user};Password={password};TrustServerCertificate=True",
            sql =>
            {
                sql.MigrationsHistoryTable("__EFMigrationsHistory_{Name}", "{schema}");
                sql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });

        return new {Name}DbContext(optionsBuilder.Options);
    }
}
```
