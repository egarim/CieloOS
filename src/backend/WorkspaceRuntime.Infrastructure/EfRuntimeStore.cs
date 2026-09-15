using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Infrastructure;

public sealed class EfRuntimeStore : IRuntimeStore
{
    private readonly IDbContextFactory<RuntimeDbContext> contextFactory;

    // Default seedDemo:true so PersistenceTests' `new EfRuntimeStore(factory)` and
    // any dev use keep the joche/yulia demo population. A shipped, provider-free
    // image is wired with seedDemo:false (see Program.cs): the machine has no
    // users, and therefore no spreadsheet row, until the first owner claims it.
    public EfRuntimeStore(IDbContextFactory<RuntimeDbContext> contextFactory, bool seedDemo = true, string? sqliteDatabasePath = null, bool ensureCreated = false)
    {
        this.contextFactory = contextFactory;
        EnsureCreatedAndSeeded(seedDemo, sqliteDatabasePath, ensureCreated);
    }

    private void EnsureCreatedAndSeeded(bool seedDemo, string? sqliteDatabasePath, bool ensureCreated)
    {
        using var context = contextFactory.CreateDbContext();

        // Tests only, and opt-in: build the schema straight from the model, so a
        // new column needs no migration. No history and no upgrade path.
        //
        // This used to be the DEFAULT, including in production, and the comment here
        // claimed "Production keeps Migrate()" while nothing in distro/ or release/
        // ever set the flag that would have made that true. The effect was that every
        // installed machine built its schema once and then never changed it: fresh
        // installs were always correct, and an existing machine taking a new release
        // crash-looped on the first query for a column no migration had ever added
        // (#49). Defaulting to Migrate() means a mistake now fails on a fresh install,
        // where it is loud and harmless, instead of on someone's running machine.
        if (ensureCreated)
        {
            context.Database.EnsureCreated();
        }
        else
        {

        // A schema built by EnsureCreated has no history at all, so there is no way
        // to tell WHICH migrations it already reflects. Migrate() would try to create
        // tables that are already there and fail with something about a duplicate
        // table, which reads like a corrupt database rather than a recoverable one.
        // Say what actually happened, and leave the data untouched.
        var appliedMigrations = context.Database.GetAppliedMigrations().ToList();
        if (appliedMigrations.Count == 0
            && context.Database.GetService<IRelationalDatabaseCreator>().HasTables())
        {
            throw new InvalidOperationException(
                "This database has tables but no migration history, so it was created by a build that "
                + "used EnsureCreated (#49). Which schema version it holds cannot be determined, and "
                + "migrating it blindly would risk the data. Export what you need, or stamp "
                + "__EFMigrationsHistory to the version this schema actually matches, then restart.");
        }

        // A DB that was migrated by a newer build contains history rows this build
        // does not know about. Applying this build's migrations to it would either
        // no-op or fail later in a query; refusing here makes the rollback path
        // explicit and points at the backup that the newer build should have left.
        var knownMigrations = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var unknownAppliedMigrations = appliedMigrations
            .Where(migration => !knownMigrations.Contains(migration))
            .ToList();
        if (unknownAppliedMigrations.Count > 0)
        {
            var newestUnknown = unknownAppliedMigrations[^1];
            var backup = FindMostRecentBackup(sqliteDatabasePath);
            var backupMessage = backup is null
                ? $"No timestamped backup was found beside '{sqliteDatabasePath}'."
                : $"Restore from the timestamped backup at '{backup}'.";
            throw new InvalidOperationException(
                $"The database schema is newer than this build understands (applied migration '{newestUnknown}' is not present in this binary). {backupMessage}");
        }

        // Only take a copy when Migrate() has work to do; an up-to-date DB must
        // not gain a new backup file on every boot.
        if (sqliteDatabasePath is not null
            && File.Exists(sqliteDatabasePath)
            && context.Database.GetPendingMigrations().Any())
        {
            var backupPath = BackupSqliteDatabase(sqliteDatabasePath);
            Console.Error.WriteLine($"Database backup written to {backupPath}");
        }

        context.Database.Migrate();
        }

        // Per-entity guards, NOT a global `Users.Any()` early-return: a non-demo
        // machine has no users for many boots (until it is claimed), and it must
        // still get its spreadsheet singleton exactly once — the control plane
        // reads `.Single(Id == 1)` on nearly every operation.
        PlatformUser? seededFirstUser = null;
        if (seedDemo && !context.Users.Any())
        {
            AgentProfile? firstAgent = null;
            foreach (var (user, workspace, agent) in RuntimeSeed.People())
            {
                seededFirstUser ??= user;
                firstAgent ??= agent;
                context.Users.Add(new UserRow { Id = user.Id, DisplayName = user.DisplayName, Email = user.Email, Slug = user.Slug, DeskProfile = user.DeskProfile, Language = user.Language });
                context.Workspaces.Add(new WorkspaceRow { Id = workspace.Id, OwnerUserId = workspace.OwnerUserId, Name = workspace.Name });
                context.Agents.Add(ToAgentRow(agent));
            }

            context.AuditEvents.Add(ToRow(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, seededFirstUser!.Id, firstAgent!.Id, "runtime.seed", AuditOutcome.Success, "Seeded demo users.")));
        }

        if (seedDemo)
        {
            var ownerSlug = seededFirstUser?.Slug
                ?? context.Users.AsNoTracking().OrderBy(user => user.Slug).First().Slug;
            var row = EnsureSpreadsheetForOwner(context, ownerSlug, out var created);
            if (created)
            {
                row.CellsJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["A1"] = "12",
                    ["A2"] = "30",
                    ["B1"] = "Ready"
                });
            }
        }

        context.SaveChanges();
    }

    private static string BackupSqliteDatabase(string sqliteDatabasePath)
    {
        var fullPath = Path.GetFullPath(sqliteDatabasePath);
        var backupPath = $"{fullPath}.{DateTime.Now:yyyyMMddHHmmssfff}.bak";
        File.Copy(fullPath, backupPath, overwrite: true);
        return backupPath;
    }

    private static string? FindMostRecentBackup(string? sqliteDatabasePath)
    {
        if (string.IsNullOrWhiteSpace(sqliteDatabasePath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(sqliteDatabasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        var databaseName = Path.GetFileName(fullPath);
        return Directory.EnumerateFiles(directory, $"{databaseName}.*")
            .Where(path => path.EndsWith(".bak", StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public IReadOnlyList<PlatformUser> Users
    {
        get
        {
            using var context = contextFactory.CreateDbContext();
            return context.Users.AsNoTracking().Select(row => new PlatformUser(row.Id, row.DisplayName, row.Email, row.Slug, row.DeskProfile, row.Language)).ToList();
        }
    }

    public IReadOnlyList<Workspace> Workspaces
    {
        get
        {
            using var context = contextFactory.CreateDbContext();
            return context.Workspaces.AsNoTracking().Select(row => new Workspace(row.Id, row.OwnerUserId, row.Name)).ToList();
        }
    }

    public IReadOnlyList<AgentProfile> Agents
    {
        get
        {
            using var context = contextFactory.CreateDbContext();
            return context.Agents.AsNoTracking().AsEnumerable().Select(ToAgent).ToList();
        }
    }

    public IReadOnlyList<ApprovalRecord> Approvals
    {
        get
        {
            using var context = contextFactory.CreateDbContext();
            return context.Approvals.AsNoTracking().AsEnumerable().Select(ToApproval).OrderByDescending(approval => approval.CreatedAt).ToList();
        }
    }

    public IReadOnlyList<AuditEvent> AuditEvents
    {
        get
        {
            using var context = contextFactory.CreateDbContext();
            return context.AuditEvents.AsNoTracking().AsEnumerable().Select(ToAudit).OrderByDescending(auditEvent => auditEvent.OccurredAt).ToList();
        }
    }

    public SpreadsheetState GetSpreadsheet(string ownerSlug)
    {
        using var context = contextFactory.CreateDbContext();
        var row = context.Spreadsheets.AsNoTracking().SingleOrDefault(sheet => sheet.OwnerSlug == ownerSlug);
        return row is null
            ? new SpreadsheetState(new Dictionary<string, string>())
            : new SpreadsheetState(JsonSerializer.Deserialize<Dictionary<string, string>>(row.CellsJson) ?? new Dictionary<string, string>());
    }

    public long GetSpreadsheetRevision(string ownerSlug)
    {
        using var context = contextFactory.CreateDbContext();
        return context.Spreadsheets.AsNoTracking().SingleOrDefault(sheet => sheet.OwnerSlug == ownerSlug)?.Revision ?? 0;
    }
    public string? PasswordHashFor(Guid userId)
    {
        using var context = contextFactory.CreateDbContext();
        return EfPasswords.Read(context, userId);
    }

    public void SetPasswordHash(Guid userId, string hash)
    {
        using var context = contextFactory.CreateDbContext();
        EfPasswords.Write(context, userId, hash);
    }

    public void SetLanguage(Guid userId, string language)
    {
        using var context = contextFactory.CreateDbContext();
        var row = context.Users.FirstOrDefault(user => user.Id == userId);
        if (row is null)
        {
            return;
        }
        row.Language = language;
        context.SaveChanges();
    }


    public PlatformUser GetUser(Guid id)
    {
        using var context = contextFactory.CreateDbContext();
        var row = context.Users.AsNoTracking().Single(user => user.Id == id);
        return new PlatformUser(row.Id, row.DisplayName, row.Email, row.Slug, row.DeskProfile, row.Language);
    }

    public AgentProfile GetAgent(Guid id)
    {
        using var context = contextFactory.CreateDbContext();
        return ToAgent(context.Agents.AsNoTracking().Single(agent => agent.Id == id));
    }

    public ApprovalRecord GetApproval(Guid id)
    {
        using var context = contextFactory.CreateDbContext();
        return ToApproval(context.Approvals.AsNoTracking().Single(approval => approval.Id == id));
    }

    public void UpsertApproval(ApprovalRecord approval)
    {
        using var context = contextFactory.CreateDbContext();
        var existing = context.Approvals.SingleOrDefault(row => row.Id == approval.Id);
        if (existing is null)
        {
            existing = new ApprovalRow { Id = approval.Id };
            context.Approvals.Add(existing);
        }

        existing.ToolRequestId = approval.ToolRequestId;
        existing.UserId = approval.UserId;
        existing.Status = approval.Status.ToString();
        existing.Reason = approval.Reason;
        existing.CreatedAt = approval.CreatedAt;
        existing.ResolvedAt = approval.ResolvedAt;
        existing.RequestHash = approval.RequestHash;
        context.SaveChanges();
    }

    public void AppendAudit(AuditEvent auditEvent)
    {
        using var context = contextFactory.CreateDbContext();
        context.AuditEvents.Add(ToRow(auditEvent));
        context.SaveChanges();
    }

    public void SetSpreadsheet(string ownerSlug, SpreadsheetState spreadsheet)
    {
        using var context = contextFactory.CreateDbContext();
        var row = EnsureSpreadsheetForOwner(context, ownerSlug, out _);
        row.CellsJson = JsonSerializer.Serialize(spreadsheet.Cells);
        row.Revision++;
        context.SaveChanges();
    }

    public void SavePendingRequest(Guid approvalId, ToolRequest request)
    {
        using var context = contextFactory.CreateDbContext();
        var existing = context.PendingRequests.SingleOrDefault(row => row.ApprovalId == approvalId);
        if (existing is null)
        {
            existing = new PendingRequestRow { ApprovalId = approvalId };
            context.PendingRequests.Add(existing);
        }

        existing.RequestJson = JsonSerializer.Serialize(new StoredToolRequest(
            request.Id, request.UserId, request.AgentId, request.ToolName, request.Operation,
            new Dictionary<string, string>(request.Arguments), request.CreatedAt));
        context.SaveChanges();
    }

    public ToolRequest GetPendingRequest(Guid approvalId) =>
        FindPendingRequest(approvalId)
            ?? throw new InvalidOperationException("Pending request was not found.");

    public ToolRequest? FindPendingRequest(Guid approvalId)
    {
        using var context = contextFactory.CreateDbContext();
        var row = context.PendingRequests.AsNoTracking().SingleOrDefault(pending => pending.ApprovalId == approvalId);
        if (row is null)
        {
            return null;
        }

        var stored = JsonSerializer.Deserialize<StoredToolRequest>(row.RequestJson)
            ?? throw new InvalidOperationException("Pending request payload was unreadable.");
        return new ToolRequest(stored.Id, stored.UserId, stored.AgentId, stored.ToolName, stored.Operation, stored.Arguments, stored.CreatedAt);
    }

    public RuntimePrincipal? FindPrincipalBySlug(string slug) =>
        PrincipalResolver.BySlug(Users, Agents, slug);

    public IReadOnlyList<WorkspaceRuntime.Domain.Thread> Threads
    {
        get
        {
            using var context = contextFactory.CreateDbContext();
            return context.Threads.AsNoTracking()
                .OrderByDescending(thread => thread.LastActivityAtTicks)
                .AsEnumerable()
                .Select(ToThread)
                .ToList();
        }
    }

    public WorkspaceRuntime.Domain.Thread CreateThread(string ownerSlug, string title, string firstMessage)
    {
        using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var threadId = Guid.NewGuid();
        context.Threads.Add(new ThreadRow
        {
            Id = threadId,
            OwnerSlug = ownerSlug,
            Title = title,
            State = ThreadStatus.Working.ToString(),
            CreatedAt = now,
            CreatedAtTicks = now.UtcTicks,
            LastActivityAt = now,
            LastActivityAtTicks = now.UtcTicks
        });
        context.ThreadMessages.Add(new ThreadMessageRow
        {
            Id = Guid.NewGuid(),
            ThreadId = threadId,
            Role = ThreadMessageRole.Person.ToString(),
            Text = firstMessage,
            CreatedAt = now,
            CreatedAtTicks = now.UtcTicks,
            Sequence = 1
        });
        context.SaveChanges();
        return new WorkspaceRuntime.Domain.Thread(threadId, ownerSlug, title, ThreadStatus.Working, now, now);
    }

    // Reading MAX(Sequence) and inserting are two statements, so two concurrent
    // messages can both read the same maximum and claim the same position. The unique
    // index on (ThreadId, Sequence) makes a collision impossible to persist; this lock
    // makes it impossible to attempt, so the common case never has to handle a
    // constraint violation. A second RUNTIME PROCESS against the same Postgres would
    // still need retry-on-conflict — the index is what keeps that case correct rather
    // than corrupt.
    private static readonly object appendGate = new();

    public ThreadMessage AppendThreadMessage(Guid threadId, ThreadMessageRole role, string text)
    {
        // The lock serialises appends inside THIS process; it says nothing about a
        // second one. Two runtimes sharing a Postgres can still read the same
        // MAX(Sequence), and then the unique index rejects the loser — correctly, but
        // as an unhandled 500 unless we catch it. Recompute and retry: the index is
        // what makes the retry safe, the retry is what makes it invisible.
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return AppendThreadMessageOnce(threadId, role, text);
            }
            catch (DbUpdateException) when (attempt < attempts)
            {
                // Another writer took this position. Loop and read the new maximum.
            }
        }
    }

    private ThreadMessage AppendThreadMessageOnce(Guid threadId, ThreadMessageRole role, string text)
    {
        lock (appendGate)
        {
            using var context = contextFactory.CreateDbContext();
            var row = context.Threads.SingleOrDefault(thread => thread.Id == threadId);
            if (row is null)
            {
                throw new InvalidOperationException($"Thread '{threadId}' not found.");
            }

            var now = DateTimeOffset.UtcNow;
            row.LastActivityAt = now;
            row.LastActivityAtTicks = now.UtcTicks;
            var nextSequence = (context.ThreadMessages
                .Where(message => message.ThreadId == threadId)
                .Max(message => (long?)message.Sequence) ?? 0L) + 1L;
            var message = new ThreadMessageRow
            {
                Id = Guid.NewGuid(),
                ThreadId = threadId,
                Role = role.ToString(),
                Text = text,
                CreatedAt = now,
                CreatedAtTicks = now.UtcTicks,
                Sequence = nextSequence
            };
            context.ThreadMessages.Add(message);
            context.SaveChanges();
            return ToThreadMessage(message);
        }
    }

    public IReadOnlyList<WorkspaceRuntime.Domain.Thread> ListThreadsByOwner(string ownerSlug)
    {
        using var context = contextFactory.CreateDbContext();
        return context.Threads.AsNoTracking()
            .Where(thread => thread.OwnerSlug == ownerSlug)
            .OrderByDescending(thread => thread.LastActivityAtTicks)
            .AsEnumerable()
            .Select(ToThread)
            .ToList();
    }

    public ThreadWithMessages? GetThread(Guid id)
    {
        using var context = contextFactory.CreateDbContext();
        var row = context.Threads.AsNoTracking().SingleOrDefault(thread => thread.Id == id);
        if (row is null)
        {
            return null;
        }

        var messages = context.ThreadMessages.AsNoTracking()
            .Where(message => message.ThreadId == id)
            // Sequence IS the append order. Ordering by wall clock first meant a
            // backward clock adjustment could reorder a conversation, which is the one
            // thing a transcript must never do.
            .OrderBy(message => message.Sequence)
            .AsEnumerable()
            .Select(ToThreadMessage)
            .ToList();
        return new ThreadWithMessages(ToThread(row), messages);
    }

    public void SetThreadState(Guid id, ThreadStatus state)
    {
        using var context = contextFactory.CreateDbContext();
        var row = context.Threads.SingleOrDefault(thread => thread.Id == id);
        if (row is null)
        {
            throw new InvalidOperationException($"Thread '{id}' not found.");
        }

        var now = DateTimeOffset.UtcNow;
        row.State = state.ToString();
        row.LastActivityAt = now;
        row.LastActivityAtTicks = now.UtcTicks;
        context.SaveChanges();
    }

    // Direct messages. Every one of these takes the CALLER's slug and filters on
    // it. A conversation has exactly two readers, and passing the pair in from the
    // endpoint would make "whose messages are these" a question the store answers
    // by trusting its caller — which is how the audit log came to be readable by
    // every agent token (#30).

    public IReadOnlyList<Conversation> ListConversations(string mySlug)
    {
        using var context = contextFactory.CreateDbContext();
        var mine = context.DirectMessages
            .AsNoTracking()
            .Where(row => row.FromSlug == mySlug || row.ToSlug == mySlug)
            .OrderByDescending(row => row.CreatedAtTicks)
            .ToList();

        var displayBySlug = context.Users.AsNoTracking()
            .ToDictionary(user => user.Slug, user => user.DisplayName, StringComparer.Ordinal);

        return mine
            .GroupBy(row => row.FromSlug == mySlug ? row.ToSlug : row.FromSlug, StringComparer.Ordinal)
            .Select(group =>
            {
                var newest = group.First();
                return new Conversation(
                    group.Key,
                    displayBySlug.TryGetValue(group.Key, out var display) ? display : group.Key,
                    newest.Text,
                    newest.FromSlug,
                    newest.CreatedAt,
                    group.Count(row => row.ToSlug == mySlug && row.ReadAt is null));
            })
            .OrderByDescending(conversation => conversation.LastAt)
            .ToList();
    }

    public IReadOnlyList<DirectMessage> ReadConversation(string mySlug, string withSlug)
    {
        using var context = contextFactory.CreateDbContext();
        var key = ConversationKey.For(mySlug, withSlug);
        return context.DirectMessages
            .AsNoTracking()
            // The key alone is not the check. It is derived from the two slugs, so
            // it would match for anybody who could guess the pair; the caller must
            // also actually be one of the two ends.
            .Where(row => row.ConversationKey == key && (row.FromSlug == mySlug || row.ToSlug == mySlug))
            .OrderBy(row => row.Sequence)
            .Select(row => new DirectMessage(row.Id, row.FromSlug, row.ToSlug, row.Text, row.CreatedAt, row.ReadAt))
            .ToList();
    }

    private static readonly object directMessageGate = new();

    public DirectMessage SendDirectMessage(string fromSlug, string toSlug, string text)
    {
        // Same retry-and-lock shape as the thread messages: reading the count and
        // appending must be one step, or two concurrent sends both become position
        // N and the order they were sent in is gone.
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return SendDirectMessageOnce(fromSlug, toSlug, text);
            }
            catch (DbUpdateException) when (attempt < attempts)
            {
            }
        }
    }

    private DirectMessage SendDirectMessageOnce(string fromSlug, string toSlug, string text)
    {
        lock (directMessageGate)
        {
            using var context = contextFactory.CreateDbContext();
            var key = ConversationKey.For(fromSlug, toSlug);
            var now = DateTimeOffset.UtcNow;
            var sequence = context.DirectMessages.Count(row => row.ConversationKey == key) + 1L;
            var row = new DirectMessageRow
            {
                Id = Guid.NewGuid(),
                ConversationKey = key,
                FromSlug = fromSlug,
                ToSlug = toSlug,
                Text = text,
                CreatedAt = now,
                CreatedAtTicks = now.UtcTicks,
                Sequence = sequence,
                ReadAt = null
            };
            context.DirectMessages.Add(row);
            context.SaveChanges();
            return new DirectMessage(row.Id, row.FromSlug, row.ToSlug, row.Text, row.CreatedAt, row.ReadAt);
        }
    }

    public int MarkConversationRead(string mySlug, string withSlug)
    {
        using var context = contextFactory.CreateDbContext();
        var key = ConversationKey.For(mySlug, withSlug);
        // Only what was sent TO the caller. Marking your own outgoing messages read
        // would make the other person's unread count depend on you opening the tab.
        var unread = context.DirectMessages
            .Where(row => row.ConversationKey == key && row.ToSlug == mySlug && row.ReadAt == null)
            .ToList();
        var now = DateTimeOffset.UtcNow;
        foreach (var row in unread)
        {
            row.ReadAt = now;
        }
        context.SaveChanges();
        return unread.Count;
    }

    public bool CreateOwner(PlatformUser user, Workspace workspace, AgentProfile agent)
    {
        using var context = contextFactory.CreateDbContext();

        // At-most-one-owner: re-check inside the same context that performs the
        // insert. Concurrent claims are serialized by the caller (ISetupService),
        // and this runtime is a single process, so this recheck-then-insert is the
        // authoritative guard; a second claim finds users present and no-ops.
        if (context.Users.Any())
        {
            return false;
        }

        context.Users.Add(new UserRow { Id = user.Id, DisplayName = user.DisplayName, Email = user.Email, Slug = user.Slug, DeskProfile = user.DeskProfile, Language = user.Language });
        context.Workspaces.Add(new WorkspaceRow { Id = workspace.Id, OwnerUserId = workspace.OwnerUserId, Name = workspace.Name });
        context.Agents.Add(ToAgentRow(agent));
        context.AuditEvents.Add(ToRow(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, user.Id, agent.Id, "owner.claim", AuditOutcome.Success, $"Claimed owner '{user.Slug}'.")));
        EnsureSpreadsheetForOwner(context, user.Slug, out _);
        context.SaveChanges();
        return true;
    }

    public bool AddUser(PlatformUser user, Workspace workspace, AgentProfile agent)
    {
        using var context = contextFactory.CreateDbContext();
        if (context.Users.Any(row => row.Slug == user.Slug) || context.Agents.Any(row => row.Slug == agent.Slug))
        {
            return false;
        }

        context.Users.Add(new UserRow { Id = user.Id, DisplayName = user.DisplayName, Email = user.Email, Slug = user.Slug, DeskProfile = user.DeskProfile, Language = user.Language });
        context.Workspaces.Add(new WorkspaceRow { Id = workspace.Id, OwnerUserId = workspace.OwnerUserId, Name = workspace.Name });
        context.Agents.Add(ToAgentRow(agent));
        context.AuditEvents.Add(ToRow(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, user.Id, agent.Id, "user.add", AuditOutcome.Success, $"Added user '{user.Slug}'.")));
        EnsureSpreadsheetForOwner(context, user.Slug, out _);
        context.SaveChanges();
        return true;
    }

    private static SpreadsheetRow EnsureSpreadsheetForOwner(RuntimeDbContext context, string ownerSlug, out bool created)
    {
        var existing = context.Spreadsheets.SingleOrDefault(sheet => sheet.OwnerSlug == ownerSlug);
        if (existing is not null)
        {
            created = false;
            return existing;
        }

        // Pre-owner installs had a singleton row. Hand it to the first owner so
        // that existing work is not discarded, then create fresh rows after that.
        var legacy = context.Spreadsheets.SingleOrDefault(sheet => sheet.OwnerSlug == "");
        if (legacy is not null)
        {
            legacy.OwnerSlug = ownerSlug;
            created = false;
            return legacy;
        }

        var row = new SpreadsheetRow { OwnerSlug = ownerSlug };
        context.Spreadsheets.Add(row);
        created = true;
        return row;
    }

    private static AgentRow ToAgentRow(AgentProfile agent) => new()
    {
        Id = agent.Id,
        OwnerUserId = agent.OwnerUserId,
        WorkspaceId = agent.WorkspaceId,
        Name = agent.Name,
        InferenceProvider = agent.InferenceProvider,
        GrantedToolsJson = JsonSerializer.Serialize(agent.GrantedTools),
        Slug = agent.Slug
    };

    private static AgentProfile ToAgent(AgentRow row) => new(
        row.Id,
        row.OwnerUserId,
        row.WorkspaceId,
        row.Name,
        row.InferenceProvider,
        JsonSerializer.Deserialize<HashSet<string>>(row.GrantedToolsJson) ?? new HashSet<string>(),
        row.Slug);

    private static ApprovalRecord ToApproval(ApprovalRow row) => new(
        row.Id,
        row.ToolRequestId,
        row.UserId,
        Enum.Parse<ApprovalStatus>(row.Status),
        row.Reason,
        row.CreatedAt,
        row.ResolvedAt,
        row.RequestHash);

    private static AuditEvent ToAudit(AuditEventRow row) => new(
        row.Id,
        row.OccurredAt,
        row.UserId,
        row.AgentId,
        row.Action,
        Enum.Parse<AuditOutcome>(row.Outcome),
        row.Detail,
        row.CorrelationId,
        row.Principal,
        row.OnBehalfOf,
        row.SessionId);

    private static WorkspaceRuntime.Domain.Thread ToThread(ThreadRow row) => new(
        row.Id,
        row.OwnerSlug,
        row.Title,
        Enum.Parse<ThreadStatus>(row.State),
        row.CreatedAt,
        row.LastActivityAt);

    private static ThreadMessage ToThreadMessage(ThreadMessageRow row) => new(
        row.Id,
        row.ThreadId,
        Enum.Parse<ThreadMessageRole>(row.Role),
        row.Text,
        row.CreatedAt);

    private static AuditEventRow ToRow(AuditEvent auditEvent) => new()
    {
        Id = auditEvent.Id,
        OccurredAt = auditEvent.OccurredAt,
        UserId = auditEvent.UserId,
        AgentId = auditEvent.AgentId,
        Action = auditEvent.Action,
        Outcome = auditEvent.Outcome.ToString(),
        Detail = auditEvent.Detail,
        CorrelationId = auditEvent.CorrelationId,
        Principal = auditEvent.Principal,
        OnBehalfOf = auditEvent.OnBehalfOf,
        SessionId = auditEvent.SessionId
    };

    private sealed record StoredToolRequest(
        Guid Id,
        Guid UserId,
        Guid AgentId,
        string ToolName,
        string Operation,
        Dictionary<string, string> Arguments,
        DateTimeOffset CreatedAt);
}
