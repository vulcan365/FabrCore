---
name: vulcan365-domain
description: >
  Build domain models using DDD patterns with EF Core and SQL Server.
  Use when creating aggregate roots, entities, value objects, commands, queries, event handlers,
  or EF Core entity configurations. Triggers on: "domain model", "aggregate root",
  "entity", "command handler", "query handler", "domain event", "EF Core entity", "add domain",
  "create aggregate", "value object", "BaseEntity", "IAggregateRoot",
  "IRepository", "IUnitOfWork", "ICommand", "IQuery", "domain boundary", "ef core", "entity framework",
  or any DDD modeling task.
allowed-tools: "Bash(dotnet:*)"
metadata:
  author: Vulcan365
  version: 2.0.0
---

# Vulcan365 Domain Modeling Guide

**Project:** `{AppName}.Domain` (single-project DDD architecture)
**Persistence:** EF Core (SQL Server) -- `net10.0`
**Patterns:** DDD, Repositories, Commands/Queries, Unit of Work, Domain Events, Event Publisher, Custom Mediator, Multi-Tenancy

> **Note:** Replace `{AppName}` throughout with your project name (e.g., `Curia`, `Synod`, `MyApp`).

---

## 1. Project Context

The domain infrastructure should be scaffolded at `src/{AppName}.Domain/Infrastructure/`. The base types, abstractions, persistence classes, and DI extension described below form the foundation. If starting a new project, create them. If they already exist, do NOT recreate them.

### 1.1 Database Connection

Configure your connection string in `appsettings.json` under the name `DefaultConnection`:

```
Server=localhost;Database={appname};User Id={user};Password={password};TrustServerCertificate=True
```

### 1.2 Key NuGet Packages (already referenced)

- `Microsoft.EntityFrameworkCore.SqlServer` (10.*)
- `Microsoft.EntityFrameworkCore.Design` (10.*)
- `Scrutor` (5.*) -- for assembly scanning of handlers

### 1.3 Multi-Tenancy (Optional)

For multi-tenant applications, aggregate roots that belong to a tenant implement `ITenantEntity`:

```csharp
// Infrastructure/Common/ITenantEntity.cs
namespace {AppName}.Domain.Infrastructure.Common;

public interface ITenantEntity
{
    Guid TenantId { get; }
}
```

Tenant context is provided by `ITenantContext`:

```csharp
// Infrastructure/Abstractions/ITenantContext.cs
namespace {AppName}.Domain.Infrastructure.Abstractions;

public interface ITenantContext
{
    Guid? CurrentTenantId { get; }
    Guid? CurrentUserId { get; }
    bool IsSuperAdmin { get; }
    bool HasTenant => CurrentTenantId.HasValue;
}
```

---

## 2. Folder Structure

```
{AppName}.Domain/
├── Infrastructure/                      # ALREADY SCAFFOLDED - do not recreate
│   ├── Persistence/
│   │   ├── {AppName}DbContext.cs
│   │   ├── EfRepository.cs
│   │   └── EfUnitOfWork.cs
│   ├── Mediator/
│   │   └── SimpleMediator.cs
│   ├── Events/
│   │   └── NoOpEventPublisher.cs
│   ├── Abstractions/
│   │   ├── IRepository.cs
│   │   ├── IUnitOfWork.cs
│   │   ├── ICommand.cs
│   │   ├── IQuery.cs
│   │   ├── ICommandHandler.cs
│   │   ├── IQueryHandler.cs
│   │   ├── IDomainEventHandler.cs
│   │   ├── IMediator.cs
│   │   ├── IEventPublisher.cs
│   │   └── ITenantContext.cs
│   ├── Common/
│   │   ├── IDomainEvent.cs
│   │   ├── DomainEvent.cs
│   │   ├── IHasDomainEvents.cs
│   │   ├── BaseEntity.cs
│   │   ├── IAggregateRoot.cs
│   │   └── ITenantEntity.cs
│   └── ServiceCollectionExtensions.cs   # Add{AppName}Domain extension
├── Integration/                         # External service interfaces
│   ├── AudioPreparation/
│   ├── Notifications/
│   ├── Security/
│   ├── Storage/
│   └── Transcription/
├── Meetings/                            # Aggregate Root: Meeting
│   ├── Meeting.cs                       # Aggregate root at folder root
│   ├── MeetingConfiguration.cs          # EF Core IEntityTypeConfiguration
│   ├── Entities/
│   │   ├── AgendaItem.cs
│   │   └── MeetingParticipant.cs
│   ├── ValueObjects/
│   │   ├── MeetingStatus.cs
│   │   ├── AgendaItemStatus.cs
│   │   └── ParticipantRole.cs
│   ├── Events/
│   │   ├── MeetingCreatedEvent.cs
│   │   └── MeetingStartedEvent.cs
│   ├── Commands/
│   │   ├── CreateMeetingCommand.cs
│   │   └── CreateMeetingCommandHandler.cs
│   ├── Queries/
│   │   ├── GetMeetingByIdQuery.cs
│   │   ├── GetMeetingByIdQueryHandler.cs
│   │   └── MeetingDto.cs
│   └── EventHandlers/
│       └── MeetingStartedHandler.cs
├── Recordings/                          # Aggregate Root: Recording
├── Summaries/                           # Aggregate Root: MeetingSummary
├── Tenants/                             # Aggregate Root: Tenant
├── Users/                               # Aggregate Root: User
└── Migrations/
```

### 2.1 Key Conventions

- **Aggregate root folders** are the main organizational units (e.g., `Meetings/`, `Users/`)
- **Aggregate roots** sit at the **root of their folder** (e.g., `Meetings/Meeting.cs`)
- **EF Core configurations** sit at the **root of their folder** alongside the aggregate (e.g., `Meetings/MeetingConfiguration.cs`)
- Each aggregate folder contains subfolders: `Entities/`, `ValueObjects/`, `Events/`, `Commands/`, `Queries/`, `EventHandlers/`
- **Infrastructure/** -- persistence, DI, mediator, abstractions, and common base types (already built)
- **Integration/** -- external service interfaces, implementations, and DTOs

---

## 3. Infrastructure Reference (Already Scaffolded)

These types exist at `src/{AppName}.Domain/Infrastructure/`. Reference only -- do NOT recreate them.

### 3.1 Common Base Types

#### IDomainEvent

```csharp
// Infrastructure/Common/IDomainEvent.cs
namespace {AppName}.Domain.Infrastructure.Common;

public interface IDomainEvent
{
    DateTime OccurredOnUtc { get; }
}
```

#### DomainEvent

```csharp
// Infrastructure/Common/DomainEvent.cs
namespace {AppName}.Domain.Infrastructure.Common;

public abstract class DomainEvent : IDomainEvent
{
    protected DomainEvent()
    {
        OccurredOnUtc = DateTime.UtcNow;
    }

    public DateTime OccurredOnUtc { get; }
}
```

#### IHasDomainEvents

```csharp
// Infrastructure/Common/IHasDomainEvents.cs
namespace {AppName}.Domain.Infrastructure.Common;

public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }
    void AddDomainEvent(IDomainEvent domainEvent);
    void ClearDomainEvents();
}
```

#### BaseEntity

```csharp
// Infrastructure/Common/BaseEntity.cs
namespace {AppName}.Domain.Infrastructure.Common;

public abstract class BaseEntity : IHasDomainEvents
{
    private readonly List<IDomainEvent> _domainEvents = new();

    public Guid Id { get; protected set; } = Guid.NewGuid();

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void AddDomainEvent(IDomainEvent domainEvent)
        => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents()
        => _domainEvents.Clear();
}
```

> **ID strategy:** GUIDs generated on the domain side. All entities get `Guid Id` from `BaseEntity`.

#### IAggregateRoot

```csharp
// Infrastructure/Common/IAggregateRoot.cs
namespace {AppName}.Domain.Infrastructure.Common;

public interface IAggregateRoot
{
}
```

### 3.2 Abstractions

#### IRepository

```csharp
// Infrastructure/Abstractions/IRepository.cs
using {AppName}.Domain.Infrastructure.Common;

namespace {AppName}.Domain.Infrastructure.Abstractions;

public interface IRepository<TAggregate>
    where TAggregate : BaseEntity, IAggregateRoot
{
    Task<TAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<TAggregate>> ListAsync(CancellationToken ct = default);
    Task AddAsync(TAggregate entity, CancellationToken ct = default);
    void Update(TAggregate entity);
    void Remove(TAggregate entity);
}
```

> **Repository scope:** The repository is for **aggregate lifecycle** operations only. For richer reads (filtering, includes, projections), use `{AppName}DbContext` directly in query handlers.

#### IUnitOfWork

```csharp
// Infrastructure/Abstractions/IUnitOfWork.cs
namespace {AppName}.Domain.Infrastructure.Abstractions;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
```

#### ICommand and IQuery

```csharp
// Infrastructure/Abstractions/ICommand.cs
namespace {AppName}.Domain.Infrastructure.Abstractions;

public interface ICommand<TResult> { }

// Infrastructure/Abstractions/IQuery.cs
namespace {AppName}.Domain.Infrastructure.Abstractions;

public interface IQuery<TResult> { }
```

#### ICommandHandler and IQueryHandler

```csharp
// Infrastructure/Abstractions/ICommandHandler.cs
namespace {AppName}.Domain.Infrastructure.Abstractions;

public interface ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken ct = default);
}

// Infrastructure/Abstractions/IQueryHandler.cs
namespace {AppName}.Domain.Infrastructure.Abstractions;

public interface IQueryHandler<TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken ct = default);
}
```

#### IDomainEventHandler

```csharp
// Infrastructure/Abstractions/IDomainEventHandler.cs
using {AppName}.Domain.Infrastructure.Common;

namespace {AppName}.Domain.Infrastructure.Abstractions;

public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken ct = default);
}
```

#### IMediator

```csharp
// Infrastructure/Abstractions/IMediator.cs
using {AppName}.Domain.Infrastructure.Common;

namespace {AppName}.Domain.Infrastructure.Abstractions;

public interface IMediator
{
    Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default);
    Task<TResult> SendAsync<TResult>(IQuery<TResult> query, CancellationToken ct = default);
    Task PublishDomainEventAsync(IDomainEvent domainEvent, CancellationToken ct = default);
}
```

#### IEventPublisher

```csharp
// Infrastructure/Abstractions/IEventPublisher.cs
using {AppName}.Domain.Infrastructure.Common;

namespace {AppName}.Domain.Infrastructure.Abstractions;

public interface IEventPublisher
{
    Task PublishAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken ct = default);
}
```

### 3.3 Persistence

#### {AppName}DbContext

```csharp
// Infrastructure/Persistence/{AppName}DbContext.cs
using Microsoft.EntityFrameworkCore;
using {AppName}.Domain.Infrastructure.Common;

namespace {AppName}.Domain.Infrastructure.Persistence;

public class {AppName}DbContext : DbContext
{
    public {AppName}DbContext(DbContextOptions<{AppName}DbContext> options)
        : base(options)
    {
    }

    // Add DbSet<T> for each aggregate and entity here
    // public DbSet<Meeting> Meetings => Set<Meeting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Apply IEntityTypeConfiguration classes via:
        // modelBuilder.ApplyConfigurationsFromAssembly(typeof({AppName}DbContext).Assembly);
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

#### EfRepository

```csharp
// Infrastructure/Persistence/EfRepository.cs
using Microsoft.EntityFrameworkCore;
using {AppName}.Domain.Infrastructure.Abstractions;
using {AppName}.Domain.Infrastructure.Common;

namespace {AppName}.Domain.Infrastructure.Persistence;

public sealed class EfRepository<TAggregate> : IRepository<TAggregate>
    where TAggregate : BaseEntity, IAggregateRoot
{
    private readonly {AppName}DbContext _dbContext;
    private readonly DbSet<TAggregate> _dbSet;

    public EfRepository({AppName}DbContext dbContext)
    {
        _dbContext = dbContext;
        _dbSet = dbContext.Set<TAggregate>();
    }

    public async Task<TAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _dbSet.FindAsync(new object?[] { id }, ct);

    public async Task<IReadOnlyList<TAggregate>> ListAsync(CancellationToken ct = default)
        => await _dbSet.ToListAsync(ct);

    public async Task AddAsync(TAggregate entity, CancellationToken ct = default)
        => await _dbSet.AddAsync(entity, ct);

    public void Update(TAggregate entity)
        => _dbSet.Update(entity);

    public void Remove(TAggregate entity)
        => _dbSet.Remove(entity);
}
```

#### EfUnitOfWork

```csharp
// Infrastructure/Persistence/EfUnitOfWork.cs
using {AppName}.Domain.Infrastructure.Abstractions;

namespace {AppName}.Domain.Infrastructure.Persistence;

public sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly {AppName}DbContext _dbContext;
    private readonly IMediator _mediator;
    private readonly IEventPublisher _eventPublisher;

    public EfUnitOfWork(
        {AppName}DbContext dbContext,
        IMediator mediator,
        IEventPublisher eventPublisher)
    {
        _dbContext = dbContext;
        _mediator = mediator;
        _eventPublisher = eventPublisher;
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var domainEvents = _dbContext.CollectDomainEvents();
        var result = await _dbContext.SaveChangesAsync(ct);

        if (domainEvents.Count > 0)
        {
            foreach (var domainEvent in domainEvents)
            {
                await _mediator.PublishDomainEventAsync(domainEvent, ct);
            }

            await _eventPublisher.PublishAsync(domainEvents, ct);
        }

        return result;
    }
}
```

### 3.4 Mediator Implementation

```csharp
// Infrastructure/Mediator/SimpleMediator.cs
using Microsoft.Extensions.DependencyInjection;
using {AppName}.Domain.Infrastructure.Abstractions;
using {AppName}.Domain.Infrastructure.Common;

namespace {AppName}.Domain.Infrastructure.Mediator;

public sealed class SimpleMediator : IMediator
{
    private readonly IServiceProvider _serviceProvider;

    public SimpleMediator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<TResult> SendAsync<TResult>(
        ICommand<TResult> command,
        CancellationToken ct = default)
    {
        var commandType = command.GetType();
        var handlerType = typeof(ICommandHandler<,>).MakeGenericType(commandType, typeof(TResult));

        dynamic handler = _serviceProvider.GetRequiredService(handlerType);
        return await handler.HandleAsync((dynamic)command, ct);
    }

    public async Task<TResult> SendAsync<TResult>(
        IQuery<TResult> query,
        CancellationToken ct = default)
    {
        var queryType = query.GetType();
        var handlerType = typeof(IQueryHandler<,>).MakeGenericType(queryType, typeof(TResult));

        dynamic handler = _serviceProvider.GetRequiredService(handlerType);
        return await handler.HandleAsync((dynamic)query, ct);
    }

    public async Task PublishDomainEventAsync(
        IDomainEvent domainEvent,
        CancellationToken ct = default)
    {
        var eventType = domainEvent.GetType();
        var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(eventType);

        var handlers = _serviceProvider.GetServices(handlerType);

        foreach (var handler in handlers)
        {
            dynamic dynHandler = handler!;
            await dynHandler.HandleAsync((dynamic)domainEvent, ct);
        }
    }
}
```

### 3.5 NoOp Event Publisher

```csharp
// Infrastructure/Events/NoOpEventPublisher.cs
using {AppName}.Domain.Infrastructure.Abstractions;
using {AppName}.Domain.Infrastructure.Common;

namespace {AppName}.Domain.Infrastructure.Events;

public sealed class NoOpEventPublisher : IEventPublisher
{
    public Task PublishAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
```

### 3.6 DI Extension Method (Add{AppName}Domain)

```csharp
// Infrastructure/ServiceCollectionExtensions.cs
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using {AppName}.Domain.Infrastructure.Abstractions;
using {AppName}.Domain.Infrastructure.Events;
using {AppName}.Domain.Infrastructure.Mediator;
using {AppName}.Domain.Infrastructure.Persistence;

namespace {AppName}.Domain.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection Add{AppName}Domain(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionStringName = "DefaultConnection")
    {
        var connectionString = configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{connectionStringName}' not found.");

        services.AddDbContext<{AppName}DbContext>(options =>
        {
            options.UseSqlServer(connectionString);
        });

        // Repositories + Unit of Work
        services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();

        // Mediator + default NoOp event publisher
        services.AddScoped<IMediator, SimpleMediator>();
        services.AddScoped<IEventPublisher, NoOpEventPublisher>();

        // Register all handlers in Domain assembly via Scrutor
        Assembly domainAssembly = typeof(ServiceCollectionExtensions).Assembly;

        services.Scan(scan => scan
            .FromAssemblies(domainAssembly)
            .AddClasses(classes => classes.AssignableTo(typeof(ICommandHandler<,>)))
                .AsImplementedInterfaces()
                .WithScopedLifetime()
            .AddClasses(classes => classes.AssignableTo(typeof(IQueryHandler<,>)))
                .AsImplementedInterfaces()
                .WithScopedLifetime()
            .AddClasses(classes => classes.AssignableTo(typeof(IDomainEventHandler<>)))
                .AsImplementedInterfaces()
                .WithScopedLifetime()
        );

        return services;
    }
}
```

> **Host wiring:** In your Web project's `Program.cs`, call `builder.Services.Add{AppName}Domain(builder.Configuration);`

---

## 4. How to Add a New Domain Boundary (Step-by-Step)

When you need to add a new aggregate root (domain boundary) to {AppName}:

### Step 1: Create the Aggregate Root Folder

Create a new top-level folder in `src/{AppName}.Domain/` named after the aggregate (PascalCase, plural for collections):

```
src/{AppName}.Domain/
└── Boards/                    # New domain boundary
    ├── Board.cs               # Aggregate root
    ├── BoardConfiguration.cs  # EF Core entity configuration
    ├── Entities/
    ├── ValueObjects/
    ├── Events/
    ├── Commands/
    ├── Queries/
    └── EventHandlers/
```

### Step 2: Define the Aggregate Root

Create the aggregate root class at the folder root. It MUST:
- Inherit from `BaseEntity`
- Implement `IAggregateRoot`
- Implement `ITenantEntity` if tenant-scoped
- Have a `private` parameterless constructor for EF Core
- Use `private set` on all properties
- Raise domain events for significant state changes

```csharp
// Boards/Board.cs
using {AppName}.Domain.Infrastructure.Common;
using {AppName}.Domain.Boards.Entities;
using {AppName}.Domain.Boards.Events;
using {AppName}.Domain.Boards.ValueObjects;

namespace {AppName}.Domain.Boards;

public class Board : BaseEntity, IAggregateRoot, ITenantEntity
{
    private readonly List<BoardMember> _members = new();

    private Board() { } // For EF Core

    public Board(
        Guid tenantId,
        string name,
        string description,
        Guid createdByUserId)
    {
        TenantId = tenantId;
        Name = name;
        Description = description;
        CreatedByUserId = createdByUserId;
        Status = BoardStatus.Active;
        CreatedAt = DateTime.UtcNow;
        AddDomainEvent(new BoardCreatedEvent(this));
    }

    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public BoardStatus Status { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public IReadOnlyCollection<BoardMember> Members => _members.AsReadOnly();

    public void Rename(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("Board name cannot be empty.", nameof(newName));

        Name = newName;
        UpdatedAt = DateTime.UtcNow;
    }

    public void AddMember(Guid userId, MemberRole role)
    {
        if (_members.Any(m => m.UserId == userId))
            throw new InvalidOperationException("User is already a member.");

        var member = new BoardMember(userId, role);
        _members.Add(member);
    }

    public void Deactivate()
    {
        if (Status == BoardStatus.Inactive)
            throw new InvalidOperationException("Board is already inactive.");

        Status = BoardStatus.Inactive;
        UpdatedAt = DateTime.UtcNow;
        AddDomainEvent(new BoardDeactivatedEvent(this));
    }
}
```

### Step 3: Add Child Entities

Child entities inherit from `BaseEntity` but do NOT implement `IAggregateRoot`. They are owned by and accessed through the aggregate root.

```csharp
// Boards/Entities/BoardMember.cs
using {AppName}.Domain.Infrastructure.Common;
using {AppName}.Domain.Boards.ValueObjects;

namespace {AppName}.Domain.Boards.Entities;

public class BoardMember : BaseEntity
{
    private BoardMember() { } // For EF Core

    public BoardMember(Guid userId, MemberRole role)
    {
        UserId = userId;
        Role = role;
        JoinedAt = DateTime.UtcNow;
    }

    public Guid UserId { get; private set; }
    public MemberRole Role { get; private set; }
    public DateTime JoinedAt { get; private set; }

    public void ChangeRole(MemberRole newRole)
    {
        Role = newRole;
    }
}
```

### Step 4: Add Value Objects

Use `record` types. No identity, compared by value, immutable.

```csharp
// Boards/ValueObjects/BoardStatus.cs
namespace {AppName}.Domain.Boards.ValueObjects;

public enum BoardStatus
{
    Active,
    Inactive,
    Archived
}

// Boards/ValueObjects/MemberRole.cs
namespace {AppName}.Domain.Boards.ValueObjects;

public enum MemberRole
{
    Chair,
    Secretary,
    Member,
    Observer
}
```

For complex value objects, use records:

```csharp
// Example: a rich value object
namespace {AppName}.Domain.Boards.ValueObjects;

public sealed record BoardSchedule(
    DayOfWeek MeetingDay,
    TimeOnly MeetingTime,
    string Timezone)
{
    public DateTimeOffset NextMeeting(DateTimeOffset from)
    {
        // calculation logic here
        throw new NotImplementedException();
    }
}
```

### Step 5: Create Domain Events

Events represent facts that have happened. Always use **past tense** naming.

```csharp
// Boards/Events/BoardCreatedEvent.cs
using {AppName}.Domain.Infrastructure.Common;

namespace {AppName}.Domain.Boards.Events;

public sealed class BoardCreatedEvent : DomainEvent
{
    public BoardCreatedEvent(Board board)
    {
        BoardId = board.Id;
        TenantId = board.TenantId;
        Name = board.Name;
    }

    public Guid BoardId { get; }
    public Guid TenantId { get; }
    public string Name { get; }
}

// Boards/Events/BoardDeactivatedEvent.cs
using {AppName}.Domain.Infrastructure.Common;

namespace {AppName}.Domain.Boards.Events;

public sealed class BoardDeactivatedEvent : DomainEvent
{
    public BoardDeactivatedEvent(Board board)
    {
        BoardId = board.Id;
        TenantId = board.TenantId;
    }

    public Guid BoardId { get; }
    public Guid TenantId { get; }
}
```

### Step 6: Define Commands and Handlers

Commands are **imperative** -- they represent intent to change state.

```csharp
// Boards/Commands/CreateBoardCommand.cs
using {AppName}.Domain.Infrastructure.Abstractions;

namespace {AppName}.Domain.Boards.Commands;

public sealed record CreateBoardCommand(
    Guid TenantId,
    string Name,
    string Description,
    Guid CreatedByUserId) : ICommand<Guid>;
```

```csharp
// Boards/Commands/CreateBoardCommandHandler.cs
using {AppName}.Domain.Infrastructure.Abstractions;

namespace {AppName}.Domain.Boards.Commands;

public sealed class CreateBoardCommandHandler
    : ICommandHandler<CreateBoardCommand, Guid>
{
    private readonly IRepository<Board> _repository;
    private readonly IUnitOfWork _unitOfWork;

    public CreateBoardCommandHandler(
        IRepository<Board> repository,
        IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Guid> HandleAsync(
        CreateBoardCommand command,
        CancellationToken ct = default)
    {
        var board = new Board(
            command.TenantId,
            command.Name,
            command.Description,
            command.CreatedByUserId);

        await _repository.AddAsync(board, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return board.Id;
    }
}
```

### Step 7: Define Queries, Handlers, and DTOs

Queries are read-only operations. Query handlers use `{AppName}DbContext` directly for full EF Core power (includes, projections, filtering).

```csharp
// Boards/Queries/BoardDto.cs
namespace {AppName}.Domain.Boards.Queries;

public sealed record BoardDto(
    Guid Id,
    string Name,
    string Description,
    string Status,
    int MemberCount,
    DateTime CreatedAt);
```

```csharp
// Boards/Queries/GetBoardByIdQuery.cs
using {AppName}.Domain.Infrastructure.Abstractions;

namespace {AppName}.Domain.Boards.Queries;

public sealed record GetBoardByIdQuery(Guid Id) : IQuery<BoardDto?>;
```

```csharp
// Boards/Queries/GetBoardByIdQueryHandler.cs
using Microsoft.EntityFrameworkCore;
using {AppName}.Domain.Infrastructure.Abstractions;
using {AppName}.Domain.Infrastructure.Persistence;

namespace {AppName}.Domain.Boards.Queries;

public sealed class GetBoardByIdQueryHandler
    : IQueryHandler<GetBoardByIdQuery, BoardDto?>
{
    private readonly {AppName}DbContext _dbContext;

    public GetBoardByIdQueryHandler({AppName}DbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<BoardDto?> HandleAsync(
        GetBoardByIdQuery query,
        CancellationToken ct = default)
    {
        var board = await _dbContext
            .Set<Board>()
            .Include(b => b.Members)
            .FirstOrDefaultAsync(b => b.Id == query.Id, ct);

        if (board is null)
            return null;

        return new BoardDto(
            board.Id,
            board.Name,
            board.Description,
            board.Status.ToString(),
            board.Members.Count,
            board.CreatedAt);
    }
}
```

List query with filtering example:

```csharp
// Boards/Queries/ListBoardsForTenantQuery.cs
using {AppName}.Domain.Infrastructure.Abstractions;

namespace {AppName}.Domain.Boards.Queries;

public sealed record ListBoardsForTenantQuery(
    Guid TenantId,
    bool IncludeInactive = false) : IQuery<IReadOnlyList<BoardDto>>;
```

```csharp
// Boards/Queries/ListBoardsForTenantQueryHandler.cs
using Microsoft.EntityFrameworkCore;
using {AppName}.Domain.Infrastructure.Abstractions;
using {AppName}.Domain.Infrastructure.Persistence;
using {AppName}.Domain.Boards.ValueObjects;

namespace {AppName}.Domain.Boards.Queries;

public sealed class ListBoardsForTenantQueryHandler
    : IQueryHandler<ListBoardsForTenantQuery, IReadOnlyList<BoardDto>>
{
    private readonly {AppName}DbContext _dbContext;

    public ListBoardsForTenantQueryHandler({AppName}DbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<BoardDto>> HandleAsync(
        ListBoardsForTenantQuery query,
        CancellationToken ct = default)
    {
        var dbQuery = _dbContext.Set<Board>()
            .Include(b => b.Members)
            .Where(b => b.TenantId == query.TenantId);

        if (!query.IncludeInactive)
            dbQuery = dbQuery.Where(b => b.Status == BoardStatus.Active);

        return await dbQuery
            .OrderBy(b => b.Name)
            .Select(b => new BoardDto(
                b.Id,
                b.Name,
                b.Description,
                b.Status.ToString(),
                b.Members.Count,
                b.CreatedAt))
            .ToListAsync(ct);
    }
}
```

### Step 8: Add Domain Event Handlers

```csharp
// Boards/EventHandlers/BoardCreatedNotificationHandler.cs
using {AppName}.Domain.Infrastructure.Abstractions;
using {AppName}.Domain.Integration.Notifications;
using {AppName}.Domain.Boards.Events;

namespace {AppName}.Domain.Boards.EventHandlers;

public sealed class BoardCreatedNotificationHandler
    : IDomainEventHandler<BoardCreatedEvent>
{
    private readonly INotificationService _notificationService;

    public BoardCreatedNotificationHandler(INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    public async Task HandleAsync(
        BoardCreatedEvent domainEvent,
        CancellationToken ct = default)
    {
        // Send notification about new board creation
        await Task.CompletedTask;
    }
}
```

### Step 9: Create EF Core Entity Configuration

Place the configuration file at the **root of the aggregate folder**, alongside the aggregate root. Use `IEntityTypeConfiguration<T>`.

```csharp
// Boards/BoardConfiguration.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using {AppName}.Domain.Boards.ValueObjects;

namespace {AppName}.Domain.Boards;

public class BoardConfiguration : IEntityTypeConfiguration<Board>
{
    public void Configure(EntityTypeBuilder<Board> builder)
    {
        builder.ToTable("Boards");

        builder.HasKey(b => b.Id);

        // Tenant relationship
        builder.Property(b => b.TenantId)
            .IsRequired();

        builder.HasIndex(b => b.TenantId);

        // Properties
        builder.Property(b => b.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(b => b.Description)
            .HasMaxLength(2000);

        builder.Property(b => b.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(b => b.CreatedByUserId)
            .IsRequired();

        builder.Property(b => b.CreatedAt)
            .IsRequired();

        builder.Property(b => b.UpdatedAt);

        // Child collection - BoardMembers
        builder.HasMany(b => b.Members)
            .WithOne()
            .HasForeignKey("BoardId")
            .OnDelete(DeleteBehavior.Cascade);

        // Ignore domain events (not persisted)
        builder.Ignore(b => b.DomainEvents);
    }
}
```

Child entity configuration (separate file or same file):

```csharp
// Boards/BoardMemberConfiguration.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using {AppName}.Domain.Boards.Entities;

namespace {AppName}.Domain.Boards;

public class BoardMemberConfiguration : IEntityTypeConfiguration<BoardMember>
{
    public void Configure(EntityTypeBuilder<BoardMember> builder)
    {
        builder.ToTable("BoardMembers");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.UserId)
            .IsRequired();

        builder.Property(m => m.Role)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(m => m.JoinedAt)
            .IsRequired();

        // Ignore domain events
        builder.Ignore(m => m.DomainEvents);
    }
}
```

### Step 10: Register in {AppName}DbContext

Add `DbSet<T>` properties and apply configurations:

```csharp
// In {AppName}DbContext.cs, add:
public DbSet<Board> Boards => Set<Board>();

// In OnModelCreating, either apply individually:
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.ApplyConfiguration(new BoardConfiguration());
    modelBuilder.ApplyConfiguration(new BoardMemberConfiguration());
}

// OR apply all configurations from assembly (preferred):
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);
    modelBuilder.ApplyConfigurationsFromAssembly(typeof({AppName}DbContext).Assembly);
}
```

### Step 11: Create and Apply Migration

```bash
cd src/{AppName}.Domain
dotnet ef migrations add AddBoards --startup-project ../{AppName}.Web
dotnet ef database update --startup-project ../YourWebProject
```

---

## 5. EF Core Configuration Patterns

### 5.1 Enum Conversion (store as string)

```csharp
builder.Property(e => e.Status)
    .HasConversion<string>()
    .HasMaxLength(20)
    .IsRequired();
```

### 5.2 Owned Value Object (stored in same table)

```csharp
builder.OwnsOne(e => e.Address, a =>
{
    a.Property(p => p.Street).HasMaxLength(200).IsRequired();
    a.Property(p => p.City).HasMaxLength(100).IsRequired();
    a.Property(p => p.State).HasMaxLength(50).IsRequired();
    a.Property(p => p.PostalCode).HasMaxLength(20).IsRequired();
    a.Property(p => p.Country).HasMaxLength(100).IsRequired();
});
```

### 5.3 One-to-Many Child Collection

```csharp
builder.HasMany(e => e.Items)
    .WithOne()
    .HasForeignKey("ParentEntityId")
    .OnDelete(DeleteBehavior.Cascade);
```

### 5.4 Backing Field for Private Collections

```csharp
builder.HasMany(e => e.Items)
    .WithOne()
    .HasForeignKey("OrderId")
    .OnDelete(DeleteBehavior.Cascade);

// EF Core can access the private backing field:
builder.Navigation(e => e.Items)
    .UsePropertyAccessMode(PropertyAccessMode.Field);
```

### 5.5 Composite Index

```csharp
builder.HasIndex(e => new { e.TenantId, e.Status });
```

### 5.6 Unique Constraint

```csharp
builder.HasIndex(e => new { e.TenantId, e.Email })
    .IsUnique();
```

### 5.7 Tenant Foreign Key (without navigation property)

```csharp
builder.HasOne<Tenant>()
    .WithMany()
    .HasForeignKey(e => e.TenantId)
    .OnDelete(DeleteBehavior.Restrict);
```

### 5.8 Ignoring Domain Events

Always ignore `DomainEvents` on any entity inheriting from `BaseEntity`:

```csharp
builder.Ignore(e => e.DomainEvents);
```

---

## 6. EF Core Best Practices

### 6.1 NoTracking by Default + Command Handler Patterns

**CRITICAL:** See `references/efcore-command-patterns.md` for the complete guide to avoiding `DbUpdateConcurrencyException`.

Configure the DbContext to disable change tracking by default for read performance:

```csharp
public {AppName}DbContext(DbContextOptions<{AppName}DbContext> options)
    : base(options)
{
    ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
}
```

**Command handler rules (these prevent the recurring DbUpdateConcurrencyException):**

| Operation | Pattern |
|-----------|---------|
| **Create aggregate** | `repository.AddAsync()` + `unitOfWork.SaveChangesAsync()` |
| **Modify aggregate** | Load with `.AsTracking()`, mutate, `unitOfWork.SaveChangesAsync()` |
| **Add child entity** | Projection query to validate parent → `DbSet<Child>.Add()` + set shadow FK manually |
| **Remove child entity** | Projection query to validate parent → Load child `.AsTracking()` → `DbSet<Child>.Remove()` |
| **Modify parent + read children** | `.AsTracking().Include(children)` — OK because parent IS being modified |

**⚠️ NEVER load a parent with `.Include(Children)` just to add/remove children through the collection. This causes EF to try to UPDATE the parent row, which fails with `DbUpdateConcurrencyException`.**

```csharp
// ❌ CAUSES DbUpdateConcurrencyException — loading parent to add child via collection
var entry = await _db.Set<JournalEntry>()
    .AsTracking()
    .Include(j => j.Lines)
    .FirstOrDefaultAsync(j => j.Id == id);
entry.AddLine(accountId, debit, credit, desc);  // Modifies _lines collection
await _unitOfWork.SaveChangesAsync();  // FAILS — EF tries to UPDATE parent

// ✅ CORRECT — validate parent with projection, insert child directly
var status = await _db.Set<JournalEntry>()
    .Where(j => j.Id == id)
    .Select(j => (JournalEntryStatus?)j.Status)
    .FirstOrDefaultAsync(ct);
if (status != JournalEntryStatus.Draft) throw ...;

var line = new JournalLine(accountId, debit, credit, desc);
_db.Set<JournalLine>().Add(line);
_db.Entry(line).Property("JournalEntryId").CurrentValue = id;
await _unitOfWork.SaveChangesAsync();  // Works — clean INSERT only
```

### 6.2 ExecutionStrategy for Transient Failures

Use `CreateExecutionStrategy()` for operations that might fail transiently (network blips, SQL Server timeouts):

```csharp
var strategy = _dbContext.Database.CreateExecutionStrategy();

await strategy.ExecuteAsync(async () =>
{
    var entity = await _dbContext.Set<Board>()
        .AsTracking()
        .FirstOrDefaultAsync(b => b.Id == id);

    if (entity is null) return;

    entity.Rename("Updated");
    await _dbContext.SaveChangesAsync();
});
```

**With transactions** (transaction must be INSIDE the strategy callback):

```csharp
var strategy = _dbContext.Database.CreateExecutionStrategy();

await strategy.ExecuteAsync(async () =>
{
    await using var transaction = await _dbContext.Database.BeginTransactionAsync();
    try
    {
        // ... your operations ...
        await _dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }
    catch
    {
        await transaction.RollbackAsync();
        throw;
    }
});
```

### 6.3 Bulk Operations with ExecuteUpdate/ExecuteDelete

For batch operations, use EF Core 7+ `ExecuteUpdateAsync` and `ExecuteDeleteAsync` instead of loading entities:

```csharp
// ❌ SLOW - Loads all entities into memory
var expired = await _dbContext.Set<Meeting>()
    .Where(m => m.Status == MeetingStatus.Draft && m.CreatedAt < cutoff)
    .ToListAsync();
foreach (var m in expired) _dbContext.Remove(m);
await _dbContext.SaveChangesAsync();

// ✅ FAST - Single SQL UPDATE statement
await _dbContext.Set<Meeting>()
    .Where(m => m.Status == MeetingStatus.Draft && m.CreatedAt < cutoff)
    .ExecuteUpdateAsync(s => s
        .SetProperty(m => m.Status, MeetingStatus.Cancelled));

// ✅ FAST - Single SQL DELETE statement
await _dbContext.Set<Meeting>()
    .Where(m => m.Status == MeetingStatus.Cancelled && m.CreatedAt < cutoff)
    .ExecuteDeleteAsync();
```

### 6.4 Query Splitting to Prevent Cartesian Explosion

When loading multiple navigation collections via `Include()`, EF Core generates a single query that can cause cartesian explosion.

**Global configuration (recommended):**

```csharp
// In Add{AppName}Domain:
options.UseSqlServer(connectionString, sqlOptions =>
{
    sqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
});
```

**Per-query override when single query is better:**

```csharp
var meetings = await _dbContext.Set<Meeting>()
    .Include(m => m.AgendaItems)
    .Include(m => m.Participants)
    .AsSingleQuery()  // Override global split behavior
    .ToListAsync();
```

| Behavior | Pros | Cons |
|-----------|-------|-------|
| SplitQuery | No cartesian explosion, better for large collections | Multiple round-trips, potential consistency issues |
| SingleQuery | Single round-trip, transactional consistency | Cartesian explosion with multiple collections |

**Default to SplitQuery globally**, override with `AsSingleQuery()` for small, well-understood navigation graphs.

### 6.5 DbContext Lifetime in DI

**ASP.NET Core (Scoped by default)** — one instance per HTTP request. This is what `Add{AppName}Domain` configures.

**Background services** — create a scope for each unit of work:

```csharp
public class MyBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<{AppName}DbContext>();
        // ... use dbContext ...
    }
}
```

**Long-lived objects (actors, singletons)** — use `IDbContextFactory`:

```csharp
// Registration
builder.Services.AddDbContextFactory<{AppName}DbContext>(options =>
    options.UseSqlServer(connectionString));

// Usage
await using var db = await _dbFactory.CreateDbContextAsync();
var meeting = await db.Set<Meeting>().FindAsync(id);
```

### 6.6 Common Pitfalls

**1. DbUpdateConcurrencyException when adding children (MOST COMMON):**
```csharp
// ❌ CAUSES CONCURRENCY EXCEPTION — loading parent to add child via navigation
var parent = await _db.Set<Parent>().AsTracking().Include(p => p.Children).FirstAsync(p => p.Id == id);
parent.AddChild(new Child(...));  // EF marks parent as Modified
await _unitOfWork.SaveChangesAsync();  // FAILS - tries to UPDATE parent row

// ✅ Insert child directly with shadow FK
var child = new Child(...);
_db.Set<Child>().Add(child);
_db.Entry(child).Property("ParentId").CurrentValue = parentId;
await _unitOfWork.SaveChangesAsync();  // Works - clean INSERT
```
**See `references/efcore-command-patterns.md` for the full pattern guide.**

**2. N+1 Query Problem:**
```csharp
// ❌ N+1 queries
var meetings = await _dbContext.Set<Meeting>().ToListAsync();
foreach (var m in meetings)
    var items = m.AgendaItems; // Lazy load triggers query per meeting

// ✅ Eager loading
var meetings = await _dbContext.Set<Meeting>()
    .Include(m => m.AgendaItems)
    .ToListAsync();
```

**3. Tracking Conflicts** — Never load the same entity in two different tracked contexts simultaneously.

**4. Not Using Async** — Always use `ToListAsync()`, `FirstOrDefaultAsync()`, etc.

**5. Querying Inside Loops** — Use `.Where(x => ids.Contains(x.Id))` instead.

---

## 7. Domain Modeling Business Guide

### 6.1 The Ubiquitous Language (Glossary)

Every domain boundary must have a clear glossary. Rules:

- **One Term, One Meaning:** Do not use "Client" and "Customer" interchangeably. Pick one.
- **Context Boundaries:** If a word changes meaning between boundaries, define the context explicitly.
- **Ban Vague Verbs:** Avoid "Manage," "Process," "Handle." Use specific verbs: "Approve," "Reject," "Calculate," "Archive," "Schedule," "Cancel."

### 6.2 Entity vs Value Object Decision

Ask yourself:

| Question | Entity | Value Object |
|---|---|---|
| Does it have a unique identity that persists over time? | Yes | No |
| Does it have a lifecycle (created, modified, terminated)? | Yes | No |
| If you change one attribute, is it the "same" thing? | Yes | No (it becomes a new value) |
| Does deleting a parent make this meaningless? | Child entity | Could be either |

**Entities** must document:
1. **Identity:** What makes it unique? (e.g., Email? Tax ID? Auto-generated GUID?)
2. **Lifecycle:** How is it created? What can change? When is it terminated?

**Value Objects** must document:
1. **Immutability:** Confirm no lifecycle.
2. **Validation:** What makes the value valid? (e.g., "EmailAddress must contain @")

### 6.3 Aggregates (Consistency Units)

Ask: "If I delete Object A, does Object B make sense anymore?"

- If deleting an **Order** means **LineItems** must also vanish, they form one aggregate.
- The Order is the **aggregate root**; LineItems are **child entities**.
- External references to entities inside an aggregate should go through the root (by root ID).

### 6.4 Domain Event Naming Conventions

| Type | Naming | Format | Examples |
|---|---|---|---|
| **Events** | Past tense (facts) | `{Noun}{Verb}Event` | `MeetingCreatedEvent`, `AgendaPublishedEvent`, `ParticipantInvitedEvent` |
| **Commands** | Imperative (intent) | `{Verb}{Noun}Command` | `CreateMeetingCommand`, `PublishAgendaCommand`, `InviteParticipantCommand` |
| **Queries** | Descriptive | `{Get/List}{Noun}Query` | `GetMeetingByIdQuery`, `ListMeetingsForUserQuery` |
| **DTOs** | Noun + Dto | `{Noun}Dto` | `MeetingDto`, `MeetingDetailDto`, `AgendaItemDto` |
| **Handlers** | Match command/query/event | `{CommandName}Handler` | `CreateMeetingCommandHandler`, `MeetingCreatedHandler` |

### 6.5 Business Rules and Invariants

Document rules as logic statements. Distinguish between:

- **Physics** (Impossible): "An agenda item can NEVER have a negative duration."
- **Policy** (Forbidden): "A meeting cannot be started UNLESS the agenda is published."
- **Legal** (Compliance): "Meeting minutes must be retained for 7 years ALWAYS."

### 6.6 State Machines

For any entity with a status, document:
- **Nodes:** The allowed statuses
- **Edges:** The actions that transition between statuses
- **Forbidden Transitions:** Explicitly state which transitions are illegal

Example for MeetingStatus:

```
Draft --> Scheduled  (via: ScheduleMeeting)
Scheduled --> InProgress  (via: StartMeeting)
InProgress --> Completed  (via: EndMeeting)
Scheduled --> Cancelled  (via: CancelMeeting)
Completed --> Archived  (via: ArchiveMeeting)

FORBIDDEN:
- Completed --> Draft
- Cancelled --> InProgress
- Archived --> any other status
```

---

## 8. Migration Instructions

**CRITICAL:** Never manually edit, delete, rename, or copy migration files. Always use EF Core CLI commands to manage migrations (except for adding custom SQL in `Up()`/`Down()`).

All migration commands must be run from `src/{AppName}.Domain` with the startup project pointing to `{AppName}.Web`.

### Create a new migration

```bash
cd src/{AppName}.Domain
dotnet ef migrations add MigrationName --startup-project ../YourWebProject
```

### Apply migrations to the database

```bash
cd src/{AppName}.Domain
dotnet ef database update --startup-project ../YourWebProject
```

### Remove the last migration (if not yet applied)

```bash
cd src/{AppName}.Domain
dotnet ef migrations remove --startup-project ../YourWebProject
```

### Generate a SQL script (for production deployments)

```bash
cd src/{AppName}.Domain
dotnet ef migrations script --startup-project ../YourWebProject --output migration.sql
```

### Generate an idempotent SQL script (safe to run multiple times)

```bash
cd src/{AppName}.Domain
dotnet ef migrations script --idempotent --startup-project ../{AppName}.Web --output migration.sql
```

---

## 9. Testing Strategy

- **Domain tests (unit):** Test aggregates and value objects with plain xUnit. No EF Core or DI -- pure domain logic and domain event assertions.
- **Handler tests (integration):** Use `Sqlite` or `InMemory` EF Core provider. Verify handlers change state correctly and raise expected domain events.
- **Event and publishing tests:** Stub or fake `IEventPublisher` to capture published events. Stub integration services.
- **Host tests (end-to-end):** Use `WebApplicationFactory` to test HTTP endpoints through the full stack.

### In-Memory Provider (Unit Tests Only)

```csharp
// Only for simple unit tests - doesn't match real database behavior
var options = new DbContextOptionsBuilder<{AppName}DbContext>()
    .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
    .Options;

using var context = new {AppName}DbContext(options);
```

### Real Database with TestContainers (Integration Tests)

```csharp
// Use real SQL Server in container for accurate behavior
var container = new MsSqlBuilder()
    .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
    .Build();

await container.StartAsync();

var options = new DbContextOptionsBuilder<{AppName}DbContext>()
    .UseSqlServer(container.GetConnectionString())
    .Options;
```

---

## 10. Quick Reference Checklist

When adding a new domain boundary, verify:

- [ ] Aggregate root folder created at `src/{AppName}.Domain/{BoundaryName}/`
- [ ] Aggregate root inherits `BaseEntity`, implements `IAggregateRoot` (and `ITenantEntity` if tenant-scoped)
- [ ] Private parameterless constructor for EF Core
- [ ] All properties use `private set`
- [ ] Domain events raised for significant state changes
- [ ] `IEntityTypeConfiguration<T>` created at folder root
- [ ] `DomainEvents` ignored in EF configuration
- [ ] `DbSet<T>` added to `{AppName}DbContext`
- [ ] Configuration applied in `OnModelCreating`
- [ ] Commands defined as `sealed record` implementing `ICommand<TResult>`
- [ ] Command handlers implement `ICommandHandler<TCommand, TResult>`
- [ ] Queries defined as `sealed record` implementing `IQuery<TResult>`
- [ ] Query handlers implement `IQueryHandler<TQuery, TResult>` and use `{AppName}DbContext` directly
- [ ] DTOs are `sealed record` types
- [ ] Event handlers implement `IDomainEventHandler<TEvent>`
- [ ] Migration created and applied
- [ ] Glossary terms defined for the boundary
- [ ] State machines documented for status-bearing entities
- [ ] Business rules listed as logic statements
