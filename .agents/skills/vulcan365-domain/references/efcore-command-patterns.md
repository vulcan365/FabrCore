# EF Core Command Handler Patterns

## CRITICAL: DbUpdateConcurrencyException Prevention

The `DbContext` defaults to `QueryTrackingBehavior.NoTracking` for read performance. This means **every write operation requires explicit tracking strategy**.

The most common error is `DbUpdateConcurrencyException: The database operation was expected to affect 1 row(s), but actually affected 0 row(s)`. This happens when EF tries to UPDATE a parent entity that was loaded without proper tracking, or when loading a parent with `.Include(children)` and adding/removing children through the navigation collection.

### Root Cause

When you load a parent entity with `.Include(Children)` and add a child via the aggregate's collection method, EF Core:
1. Detects the navigation collection changed
2. Marks the PARENT as `Modified`
3. Tries to UPDATE the parent row
4. The UPDATE fails because the parent wasn't properly tracked (NoTracking default) or because EF's original values don't match the DB

This happens **regardless of whether you use `.AsTracking()`** on the query in many cases involving owned types (`OwnsOne`) and shadow foreign keys.

---

## The Fix: Operation-Specific Handler Patterns

### Pattern 1: Creating a NEW Aggregate Root

Use `IRepository<T>.AddAsync()` — the repository calls `DbSet.Add()` which always works.

```csharp
public sealed class CreateBoardCommandHandler : ICommandHandler<CreateBoardCommand, Guid>
{
    private readonly IRepository<Board> _repository;
    private readonly IUnitOfWork _unitOfWork;

    public async Task<Guid> HandleAsync(CreateBoardCommand command, CancellationToken ct = default)
    {
        var board = new Board(command.Name, command.Description);

        await _repository.AddAsync(board, ct);
        await _unitOfWork.SaveChangesAsync(ct);

        return board.Id;
    }
}
```

### Pattern 2: Modifying an Aggregate Root (no children involved)

Load the entity with `.AsTracking()`, mutate it, save via UnitOfWork.

```csharp
public sealed class RenameBoardCommandHandler : ICommandHandler<RenameBoardCommand, bool>
{
    private readonly XxxDbContext _db;
    private readonly IUnitOfWork _unitOfWork;

    public async Task<bool> HandleAsync(RenameBoardCommand command, CancellationToken ct = default)
    {
        var board = await _db.Set<Board>()
            .AsTracking()
            .FirstOrDefaultAsync(b => b.Id == command.BoardId, ct)
            ?? throw new InvalidOperationException("Board not found.");

        board.Rename(command.NewName);

        await _unitOfWork.SaveChangesAsync(ct);
        return true;
    }
}
```

### Pattern 3: Adding a Child Entity ⚠️ CRITICAL

**DO NOT** load the parent with `.Include(Children)` and add through the collection. This causes the concurrency exception.

**DO**: Validate parent state with a projection query, then insert the child directly via `DbSet.Add()` with the shadow FK set explicitly.

```csharp
public sealed class AddBoardMemberCommandHandler : ICommandHandler<AddBoardMemberCommand, Guid>
{
    private readonly XxxDbContext _db;
    private readonly IUnitOfWork _unitOfWork;

    public async Task<Guid> HandleAsync(AddBoardMemberCommand command, CancellationToken ct = default)
    {
        // Step 1: Validate parent state with a lightweight projection (no tracking needed)
        var parentStatus = await _db.Set<Board>()
            .Where(b => b.Id == command.BoardId)
            .Select(b => (BoardStatus?)b.Status)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Board not found.");

        if (parentStatus != BoardStatus.Active)
            throw new InvalidOperationException("Can only add members to active boards.");

        // Step 2: Check for duplicates if needed
        var exists = await _db.Set<BoardMember>()
            .AnyAsync(m => m.UserId == command.UserId
                && EF.Property<Guid>(m, "BoardId") == command.BoardId, ct);
        if (exists)
            throw new InvalidOperationException("User is already a member.");

        // Step 3: Create the child entity via its constructor (domain validation happens here)
        var member = new BoardMember(command.UserId, command.Role);

        // Step 4: Add directly to DbSet and set the shadow FK
        _db.Set<BoardMember>().Add(member);
        _db.Entry(member).Property("BoardId").CurrentValue = command.BoardId;

        // Step 5: Save via UnitOfWork
        await _unitOfWork.SaveChangesAsync(ct);

        return member.Id;
    }
}
```

**Why this works:**
- No parent entity is loaded for tracking, so no UPDATE is attempted on the parent row
- The child entity is explicitly `Added`, so EF generates a clean INSERT
- The shadow FK is set manually, creating the relationship
- Domain validation still happens in the child's constructor
- Parent state validation uses a cheap SELECT projection

### Pattern 4: Removing a Child Entity

Load the child directly with tracking, remove it.

```csharp
public sealed class RemoveBoardMemberCommandHandler : ICommandHandler<RemoveBoardMemberCommand, bool>
{
    private readonly XxxDbContext _db;
    private readonly IUnitOfWork _unitOfWork;

    public async Task<bool> HandleAsync(RemoveBoardMemberCommand command, CancellationToken ct = default)
    {
        // Step 1: Validate parent state
        var parentStatus = await _db.Set<Board>()
            .Where(b => b.Id == command.BoardId)
            .Select(b => (BoardStatus?)b.Status)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Board not found.");

        if (parentStatus != BoardStatus.Active)
            throw new InvalidOperationException("Can only remove members from active boards.");

        // Step 2: Load the child with tracking
        var member = await _db.Set<BoardMember>()
            .AsTracking()
            .FirstOrDefaultAsync(m => m.Id == command.MemberId, ct)
            ?? throw new InvalidOperationException("Member not found.");

        // Step 3: Remove directly
        _db.Set<BoardMember>().Remove(member);

        await _unitOfWork.SaveChangesAsync(ct);
        return true;
    }
}
```

### Pattern 5: Modifying Parent with Children (e.g., Post with debit=credit check)

When you need to read children to validate an invariant AND modify the parent, load with `.AsTracking()` and `.Include()`. This works because you're actually modifying the parent row.

```csharp
public sealed class PostJournalEntryCommandHandler : ICommandHandler<PostJournalEntryCommand, bool>
{
    private readonly XxxDbContext _db;
    private readonly IUnitOfWork _unitOfWork;

    public async Task<bool> HandleAsync(PostJournalEntryCommand command, CancellationToken ct = default)
    {
        // Load parent WITH children WITH tracking — we ARE modifying the parent
        var entry = await _db.Set<JournalEntry>()
            .AsTracking()
            .Include(j => j.Lines)
            .FirstOrDefaultAsync(j => j.Id == command.JournalEntryId, ct)
            ?? throw new InvalidOperationException("Journal entry not found.");

        // This modifies parent properties (Status, PostedAt) and validates children
        entry.Post();

        await _unitOfWork.SaveChangesAsync(ct);
        return true;
    }
}
```

---

## Summary Decision Table

| Operation | Load Parent? | Track Parent? | Include Children? | How to save child? |
|-----------|-------------|---------------|-------------------|-------------------|
| **Create aggregate** | No | N/A | N/A | `repository.AddAsync()` |
| **Modify aggregate** | Yes (`.AsTracking()`) | Yes | Only if needed for validation | N/A |
| **Add child** | Projection only | No | No | `DbSet<Child>.Add()` + set shadow FK |
| **Remove child** | Projection only | No | No | Load child `.AsTracking()`, `DbSet<Child>.Remove()` |
| **Modify parent + read children** | Yes (`.AsTracking().Include()`) | Yes | Yes | N/A (parent is modified, not children) |

---

## CollectDomainEvents Safety

The `CollectDomainEvents()` method on the DbContext iterates `ChangeTracker.Entries()` which can trigger change detection. Always wrap it:

```csharp
public IReadOnlyCollection<IDomainEvent> CollectDomainEvents()
{
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
```
