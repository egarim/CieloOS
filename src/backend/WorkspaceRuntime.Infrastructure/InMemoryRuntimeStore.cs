using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

namespace WorkspaceRuntime.Infrastructure;

public sealed class InMemoryRuntimeStore : IRuntimeStore
{
    private readonly List<PlatformUser> users = new();
    private readonly List<Workspace> workspaces = new();
    private readonly List<AgentProfile> agents = new();
    private readonly List<ApprovalRecord> approvals = new();
    private readonly List<AuditEvent> auditEvents = new();
    private readonly Dictionary<Guid, ToolRequest> pendingRequests = new();
    private readonly Dictionary<string, SpreadsheetState> spreadsheets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> spreadsheetRevisions = new(StringComparer.Ordinal);
    private readonly List<WorkspaceRuntime.Domain.Thread> threads = new();
    private readonly List<(ThreadMessage Message, long Sequence)> threadMessages = new();
    private readonly List<DirectMessage> directMessages = new();
    private readonly List<Organization> organizations = new();
    private readonly List<Project> projects = new();
    private readonly List<ProjectMember> projectMembers = new();
    private readonly List<ProjectTask> projectTasks = new();
    private readonly List<ProjectReport> projectReports = new();
    private readonly object projectGate = new();

    // Default seedDemo:true keeps every direct `new InMemoryRuntimeStore()` (the
    // unit-test fixtures) populated with the joche/yulia demo identities. A real,
    // provider-free install constructs it with seedDemo:false — an empty machine
    // whose first owner is created by the first-run claim.
    public InMemoryRuntimeStore(bool seedDemo = true)
    {
        if (!seedDemo)
        {
            return;
        }

        foreach (var (user, workspace, agent) in RuntimeSeed.People())
        {
            users.Add(user);
            workspaces.Add(workspace);
            agents.Add(agent);
        }

        spreadsheets[users[0].Slug] = new SpreadsheetState(new Dictionary<string, string>
        {
            ["A1"] = "12",
            ["A2"] = "30",
            ["B1"] = "Ready"
        });

        auditEvents.Add(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, users[0].Id, agents[0].Id, "runtime.seed", AuditOutcome.Success, "Seeded demo users."));
    }

    public IReadOnlyList<PlatformUser> Users => users;
    public IReadOnlyList<Organization> Organizations => organizations;

    // The same four methods as EfRuntimeStore, and they are here for the same
    // reason every other pair is: this store is a SHIPPING configuration
    // (Database:Provider=memory), not a test fixture. An isolation rule enforced
    // carefully in one store and loosely in the other is a real hole in a real mode.
    public bool AddOrganization(Organization organization)
    {
        if (organizations.Any(existing => string.Equals(existing.Slug, organization.Slug, StringComparison.Ordinal)))
        {
            return false;
        }

        organizations.Add(organization);
        return true;
    }

    // ---- projects ----------------------------------------------------------
    //
    // The same shape as EfRuntimeStore, method for method, because this store is a
    // SHIPPING configuration (Database:Provider=memory) and not a test fixture. A
    // filter written carefully in one store and loosely in the other is a real hole
    // in a real mode — and the one that drifts is always the one nobody demos.
    //
    // Reads take the caller's slug FIRST and filter on it here as well as at the
    // route: a project id is guessable, and membership is the secret.

    public IReadOnlyList<ProjectDetail> ListProjectsFor(string mySlug)
    {
        lock (projectGate)
        {
            return projects
                .Where(project => Ordinal(project.LeadSlug, mySlug)
                    || projectMembers.Any(member => member.ProjectId == project.Id && Ordinal(member.MemberSlug, mySlug)))
                .OrderByDescending(project => project.CreatedAt)
                .Select(Detail)
                .ToList();
        }
    }

    public ProjectDetail? ReadProject(string mySlug, Guid projectId)
    {
        lock (projectGate)
        {
            var project = projects.FirstOrDefault(candidate => candidate.Id == projectId);
            if (project is null)
            {
                return null;
            }

            var isMember = Ordinal(project.LeadSlug, mySlug)
                || projectMembers.Any(member => member.ProjectId == projectId && Ordinal(member.MemberSlug, mySlug));
            return isMember ? Detail(project) : null;
        }
    }

    public IReadOnlyList<ProjectReport> ReadReports(string mySlug, Guid projectId)
    {
        lock (projectGate)
        {
            if (ReadProject(mySlug, projectId) is null)
            {
                return Array.Empty<ProjectReport>();
            }

            var taskIds = projectTasks.Where(task => task.ProjectId == projectId).Select(task => task.Id).ToHashSet();
            return projectReports
                .Where(report => taskIds.Contains(report.TaskId))
                .OrderBy(report => report.CreatedAt)
                .ThenBy(report => report.Sequence)
                .ToList();
        }
    }

    public Project CreateProject(string leadSlug, string orgSlug, string name)
    {
        lock (projectGate)
        {
            var now = DateTimeOffset.UtcNow;
            var project = new Project(Guid.NewGuid(), orgSlug, leadSlug, name, now);
            projects.Add(project);
            // The lead is a member of their own project, so every read is one rule.
            projectMembers.Add(new ProjectMember(Guid.NewGuid(), project.Id, leadSlug, now));
            return project;
        }
    }

    public bool AddProjectMember(Guid projectId, string memberSlug)
    {
        lock (projectGate)
        {
            if (projectMembers.Any(member => member.ProjectId == projectId && Ordinal(member.MemberSlug, memberSlug)))
            {
                return false;
            }

            projectMembers.Add(new ProjectMember(Guid.NewGuid(), projectId, memberSlug, DateTimeOffset.UtcNow));
            return true;
        }
    }

    public bool RemoveProjectMember(Guid projectId, string memberSlug)
    {
        lock (projectGate)
        {
            // RemoveAll, not Remove: if a duplicate ever existed, removing one row
            // and leaving another would leave the person the access they were just
            // removed from. The unique index makes that impossible in EF; this makes
            // it impossible here.
            return projectMembers.RemoveAll(member =>
                member.ProjectId == projectId && Ordinal(member.MemberSlug, memberSlug)) > 0;
        }
    }

    public ProjectTask? AddTask(Guid projectId, string assigneeSlug, string title)
    {
        lock (projectGate)
        {
            if (!projects.Any(project => project.Id == projectId))
            {
                return null;
            }

            var next = projectTasks.Where(task => task.ProjectId == projectId)
                .Select(task => task.Sequence)
                .DefaultIfEmpty(0)
                .Max() + 1;
            var task = new ProjectTask(
                Guid.NewGuid(), projectId, assigneeSlug, title, TaskState.Todo, "", DateTimeOffset.UtcNow, next);
            projectTasks.Add(task);
            return task;
        }
    }

    public ProjectTask? FindTask(Guid taskId)
    {
        lock (projectGate)
        {
            return projectTasks.FirstOrDefault(task => task.Id == taskId);
        }
    }

    public ProjectReport? Report(string assigneeSlug, Guid taskId, TaskState state, string text)
    {
        lock (projectGate)
        {
            var index = projectTasks.FindIndex(task => task.Id == taskId);
            // Re-filtered on the author here as well as at the route: this is the
            // write that decides whose word the trail records.
            if (index < 0 || !Ordinal(projectTasks[index].AssigneeSlug, assigneeSlug))
            {
                return null;
            }

            var next = projectReports.Where(report => report.TaskId == taskId)
                .Select(report => report.Sequence)
                .DefaultIfEmpty(0)
                .Max() + 1;
            var now = DateTimeOffset.UtcNow;
            var report = new ProjectReport(Guid.NewGuid(), taskId, assigneeSlug, state, text, now, next);
            projectReports.Add(report);
            projectTasks[index] = projectTasks[index] with { State = state, Note = text, UpdatedAt = now };
            return report;
        }
    }

    private ProjectDetail Detail(Project project) => new(
        project,
        projectMembers.Where(member => member.ProjectId == project.Id).OrderBy(member => member.AddedAt).ToList(),
        projectTasks.Where(task => task.ProjectId == project.Id).OrderBy(task => task.Sequence).ToList());

    private static bool Ordinal(string one, string other) => string.Equals(one, other, StringComparison.Ordinal);

    public Organization? FindOrganization(string slug) =>
        organizations.FirstOrDefault(organization => string.Equals(organization.Slug, slug, StringComparison.Ordinal));

    public bool SetUserOrganization(string userSlug, string orgSlug)
    {
        if (FindOrganization(orgSlug) is null)
        {
            return false;
        }

        var index = users.FindIndex(user => string.Equals(user.Slug, userSlug, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }

        var from = users[index].OrgSlug;
        users[index] = users[index] with { OrgSlug = orgSlug };
        auditEvents.Add(new AuditEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow, users[index].Id, null, "user.organization",
            AuditOutcome.Success, $"Moved '{userSlug}' from '{from}' to '{orgSlug}'."));
        return true;
    }
    public IReadOnlyList<Workspace> Workspaces => workspaces;
    public IReadOnlyList<AgentProfile> Agents => agents;
    public IReadOnlyList<ApprovalRecord> Approvals => approvals.OrderByDescending(approval => approval.CreatedAt).ToList();
    public IReadOnlyList<AuditEvent> AuditEvents => auditEvents.OrderByDescending(auditEvent => auditEvent.OccurredAt).ToList();
    public SpreadsheetState GetSpreadsheet(string ownerSlug) =>
        spreadsheets.TryGetValue(ownerSlug, out var spreadsheet)
            ? spreadsheet
            : new SpreadsheetState(new Dictionary<string, string>());

    public long GetSpreadsheetRevision(string ownerSlug) =>
        spreadsheetRevisions.TryGetValue(ownerSlug, out var revision) ? revision : 0;

    // In-memory mode keeps passwords in memory too: it exists for tests and
    // ephemeral runs, where nothing survives a restart by design.
    private readonly Dictionary<Guid, string> passwords = new();

    public string? PasswordHashFor(Guid userId) => passwords.TryGetValue(userId, out var hash) ? hash : null;

    public void SetPasswordHash(Guid userId, string hash) => passwords[userId] = hash;

    public bool SetFirstPasswordHash(Guid userId, string hash)
    {
        lock (passwords)
        {
            if (passwords.ContainsKey(userId) || users.All(user => user.Id != userId))
            {
                return false;
            }
            passwords[userId] = hash;
            return true;
        }
    }

    public void SetLanguage(Guid userId, string language)
    {
        var index = users.FindIndex(user => user.Id == userId);
        if (index >= 0)
        {
            users[index] = users[index] with { Language = language };
        }
    }

    public bool SetSuspendedAt(Guid userId, DateTimeOffset? suspendedAt)
    {
        var index = users.FindIndex(user => user.Id == userId);
        if (index < 0)
        {
            return false;
        }
        users[index] = users[index] with { SuspendedAt = suspendedAt };
        return true;
    }

    public PlatformUser GetUser(Guid id) => users.Single(user => user.Id == id);

    public AgentProfile GetAgent(Guid id) => agents.Single(agent => agent.Id == id);

    public ApprovalRecord GetApproval(Guid id) => approvals.Single(approval => approval.Id == id);

    public void UpsertApproval(ApprovalRecord approval)
    {
        approvals.RemoveAll(existing => existing.Id == approval.Id);
        approvals.Add(approval);
    }

    public void AppendAudit(AuditEvent auditEvent) => auditEvents.Add(auditEvent);

    public void SetSpreadsheet(string ownerSlug, SpreadsheetState spreadsheet)
    {
        spreadsheets[ownerSlug] = spreadsheet;
        spreadsheetRevisions[ownerSlug] = spreadsheetRevisions.TryGetValue(ownerSlug, out var revision)
            ? revision + 1
            : 1;
    }

    public void SavePendingRequest(Guid approvalId, ToolRequest request) => pendingRequests[approvalId] = request;

    public ToolRequest GetPendingRequest(Guid approvalId) =>
        pendingRequests.TryGetValue(approvalId, out var request)
            ? request
            : throw new InvalidOperationException("Pending request was not found.");

    public ToolRequest? FindPendingRequest(Guid approvalId) =>
        pendingRequests.TryGetValue(approvalId, out var request) ? request : null;

    public RuntimePrincipal? FindPrincipalBySlug(string slug) =>
        PrincipalResolver.BySlug(Users, Agents, slug);

    public IReadOnlyList<WorkspaceRuntime.Domain.Thread> Threads =>
        threads.OrderByDescending(thread => thread.LastActivityAt).ToList();

    public WorkspaceRuntime.Domain.Thread CreateThread(string ownerSlug, string title, string firstMessage)
    {
        var now = DateTimeOffset.UtcNow;
        var thread = new WorkspaceRuntime.Domain.Thread(Guid.NewGuid(), ownerSlug, title, ThreadStatus.Working, now, now);
        threads.Add(thread);
        threadMessages.Add((new ThreadMessage(Guid.NewGuid(), thread.Id, ThreadMessageRole.Person, firstMessage, now), 1L));
        return thread;
    }

    public ThreadMessage AppendThreadMessage(Guid threadId, ThreadMessageRole role, string text)
    {
        var index = threads.FindIndex(thread => thread.Id == threadId);
        if (index < 0)
        {
            throw new InvalidOperationException($"Thread '{threadId}' not found.");
        }

        // Reading the count and appending must be one step. Two concurrent messages
        // that both read "3" would both become 4, and the order they were sent in is
        // then unrecoverable — the thing the sequence exists to preserve.
        lock (threadMessages)
        {
            var now = DateTimeOffset.UtcNow;
            threads[index] = threads[index] with { LastActivityAt = now };
            var sequence = threadMessages.Count(entry => entry.Message.ThreadId == threadId) + 1L;
            var message = new ThreadMessage(Guid.NewGuid(), threadId, role, text, now);
            threadMessages.Add((message, sequence));
            return message;
        }
    }

    public IReadOnlyList<WorkspaceRuntime.Domain.Thread> ListThreadsByOwner(string ownerSlug) =>
        threads.Where(thread => string.Equals(thread.OwnerSlug, ownerSlug, StringComparison.Ordinal))
            .OrderByDescending(thread => thread.LastActivityAt)
            .ToList();

    public ThreadWithMessages? GetThread(Guid id)
    {
        var thread = threads.FirstOrDefault(candidate => candidate.Id == id);
        return thread is null
            ? null
            : new ThreadWithMessages(thread, threadMessages
                .Where(entry => entry.Message.ThreadId == id)
                .OrderBy(entry => entry.Sequence)
                .Select(entry => entry.Message)
                .ToList());
    }

    public void SetThreadState(Guid id, ThreadStatus state)
    {
        var index = threads.FindIndex(thread => thread.Id == id);
        if (index < 0)
        {
            throw new InvalidOperationException($"Thread '{id}' not found.");
        }

        threads[index] = threads[index] with { State = state, LastActivityAt = DateTimeOffset.UtcNow };
    }

    public IReadOnlyList<Conversation> ListConversations(string mySlug)
    {
        var displayBySlug = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var agent in agents)
        {
            displayBySlug[agent.Slug] = agent.Name;
        }
        foreach (var user in users)
        {
            displayBySlug[user.Slug] = user.DisplayName;
        }
        return directMessages
            .Where(message => message.FromSlug == mySlug || message.ToSlug == mySlug)
            .GroupBy(message => message.FromSlug == mySlug ? message.ToSlug : message.FromSlug, StringComparer.Ordinal)
            .Select(group =>
            {
                var newest = group.OrderByDescending(message => message.CreatedAt).First();
                return new Conversation(
                    group.Key,
                    displayBySlug.TryGetValue(group.Key, out var display) ? display : group.Key,
                    newest.Text,
                    newest.FromSlug,
                    newest.CreatedAt,
                    group.Count(message => message.ToSlug == mySlug && message.ReadAt is null));
            })
            .OrderByDescending(conversation => conversation.LastAt)
            .ToList();
    }

    public IReadOnlyList<DirectMessage> ReadConversation(string mySlug, string withSlug)
    {
        var key = ConversationKey.For(mySlug, withSlug);
        return directMessages
            .Where(message => ConversationKey.For(message.FromSlug, message.ToSlug) == key
                && (message.FromSlug == mySlug || message.ToSlug == mySlug))
            .ToList();
    }

    public DirectMessage SendDirectMessage(string fromSlug, string toSlug, string text)
    {
        lock (directMessages)
        {
            var message = new DirectMessage(Guid.NewGuid(), fromSlug, toSlug, text, DateTimeOffset.UtcNow, null);
            directMessages.Add(message);
            return message;
        }
    }

    public int MarkConversationRead(string mySlug, string withSlug)
    {
        var key = ConversationKey.For(mySlug, withSlug);
        var changed = 0;
        for (var index = 0; index < directMessages.Count; index++)
        {
            var message = directMessages[index];
            if (ConversationKey.For(message.FromSlug, message.ToSlug) == key
                && message.ToSlug == mySlug
                && message.ReadAt is null)
            {
                directMessages[index] = message with { ReadAt = DateTimeOffset.UtcNow };
                changed++;
            }
        }

        return changed;
    }

    public bool CreateOwner(PlatformUser user, Workspace workspace, AgentProfile agent)
    {
        if (users.Count > 0)
        {
            return false;
        }

        users.Add(user);
        workspaces.Add(workspace);
        agents.Add(agent);
        auditEvents.Add(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, user.Id, agent.Id, "owner.claim", AuditOutcome.Success, $"Claimed owner '{user.Slug}'."));
        return true;
    }

    public bool AddUser(PlatformUser user, Workspace workspace, AgentProfile agent)
    {
        if (users.Any(existing => existing.Slug == user.Slug) || agents.Any(existing => existing.Slug == agent.Slug))
        {
            return false;
        }

        users.Add(user);
        workspaces.Add(workspace);
        agents.Add(agent);
        auditEvents.Add(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, user.Id, agent.Id, "user.add", AuditOutcome.Success, $"Added user '{user.Slug}'."));
        return true;
    }
}
