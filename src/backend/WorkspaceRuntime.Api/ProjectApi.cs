using WorkspaceRuntime.Application;
using WorkspaceRuntime.Domain;

// Projects: work Yulia hands out, and what her team says about it.
//
// Records, never files. No route here returns a path, names a home, or resolves a
// session — see the law at the top of ProjectRules. The grep that checks it is a
// test (ProjectTests.The_law_holds), not a habit, because the existing live
// cross-user 403 test would stay green through a broken invariant: its fixtures
// have no projects in them.
//
// Every refusal is the SAME refusal, one constant body, no id echoed. A project id
// is guessable and membership is the secret, so "no such project" and "not yours"
// must be indistinguishable — the same reasoning threads and direct messages both
// already apply.
public static class ProjectApi
{
    private static readonly object NoSuchProject = new { error = "No such project." };

    public static void Map(WebApplication app)
    {
        // Everything a person is on, with members and tasks.
        app.MapGet("/api/projects", (HttpContext context, IRuntimeStore store) =>
            Results.Ok(store.ListProjectsFor(Acting(context, store)).Select(Shape)));

        // What an AGENT is allowed to know about its owner's work.
        //
        // The one exact path under this prefix that is not human-only. It returns
        // the acting user's own rows and nothing else, and the acting user for an
        // agent principal is its owner — resolved through ProjectRules.ActingUser,
        // never taken from a parameter.
        //
        // Deliberately the same shape as the prompt briefing will be clipped from,
        // so this route grants an agent no CLASS of information its prompt does not
        // already carry — only the untruncated form of it.
        app.MapGet("/api/projects/mine", (HttpContext context, IRuntimeStore store) =>
        {
            var me = Acting(context, store);
            return Results.Ok(store.ListProjectsFor(me).Select(detail => new
            {
                detail.Project.Id,
                detail.Project.Name,
                lead = detail.Project.LeadSlug,
                // Only the caller's own tasks. An agent does not need to know what
                // the rest of the team was asked to do, and every task title is text
                // somebody else wrote.
                tasks = detail.Tasks
                    .Where(task => string.Equals(task.AssigneeSlug, me, StringComparison.Ordinal))
                    .Select(task => new { task.Id, task.Title, state = task.State.ToString() })
            }));
        });

        app.MapGet("/api/projects/{id:guid}", (Guid id, HttpContext context, IRuntimeStore store) =>
        {
            var detail = store.ReadProject(Acting(context, store), id);
            return detail is null ? Results.NotFound(NoSuchProject) : Results.Ok(Shape(detail));
        });

        // The trail of what people reported. Members and lead only.
        app.MapGet("/api/projects/{id:guid}/reports", (Guid id, HttpContext context, IRuntimeStore store) =>
        {
            var me = Acting(context, store);
            if (store.ReadProject(me, id) is null)
            {
                return Results.NotFound(NoSuchProject);
            }

            return Results.Ok(store.ReadReports(me, id).Select(report => new
            {
                report.Id,
                report.TaskId,
                author = report.AuthorSlug,
                state = report.State.ToString(),
                report.Text,
                report.CreatedAt
            }));
        });

        app.MapPost("/api/projects", (CreateProjectRequest? request, HttpContext context, IRuntimeStore store) =>
        {
            var name = (request?.Name ?? "").Trim();
            if (name.Length == 0)
            {
                return Results.BadRequest(new { error = "A project needs a name." });
            }

            var me = Acting(context, store);
            var user = store.Users.FirstOrDefault(candidate => string.Equals(candidate.Slug, me, StringComparison.Ordinal));
            if (user is null)
            {
                return Results.NotFound(NoSuchProject);
            }

            // The organization is taken from the CALLER's row, never from the
            // request. A project's organization is not something to be asked for.
            var project = store.CreateProject(me, user.OrgSlug, name);
            Audit(store, context, "project.create", $"'{me}' created project '{project.Name}' ({project.Id}).");
            return Results.Ok(new { project.Id, project.Name, org = project.OrgSlug });
        });

        app.MapPost("/api/projects/{id:guid}/members", (Guid id, MemberRequest? request, HttpContext context, IRuntimeStore store) =>
        {
            var me = Acting(context, store);
            var detail = store.ReadProject(me, id);
            if (detail is null || !ProjectRules.MayLead(me, detail.Project, store))
            {
                return Results.NotFound(NoSuchProject);
            }

            var candidate = (request?.Slug ?? "").Trim();
            if (!ProjectRules.MayBeAdded(candidate, detail.Project, store))
            {
                // Same body as "no such project": whether a slug names somebody in
                // another organization is exactly what must not be discoverable.
                return Results.NotFound(NoSuchProject);
            }

            if (!store.AddProjectMember(id, candidate))
            {
                return Results.Conflict(new { error = "They are already on this project." });
            }

            Audit(store, context, "project.member.add", $"'{me}' added '{candidate}' to project {id}.");
            return Results.Ok(new { project = id, member = candidate });
        });

        app.MapDelete("/api/projects/{id:guid}/members/{slug}", (Guid id, string slug, HttpContext context, IRuntimeStore store) =>
        {
            var me = Acting(context, store);
            var detail = store.ReadProject(me, id);
            if (detail is null || !ProjectRules.MayLead(me, detail.Project, store))
            {
                return Results.NotFound(NoSuchProject);
            }

            if (string.Equals(slug, detail.Project.LeadSlug, StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = "The lead cannot be removed from their own project." });
            }

            if (!store.RemoveProjectMember(id, slug))
            {
                return Results.NotFound(NoSuchProject);
            }

            // Worth being plain about in the audit, because it is easy to believe
            // this does more than it does: it removes their access to these ROWS. It
            // does not revoke their token, end their sessions or stop their agent.
            Audit(store, context, "project.member.remove", $"'{me}' removed '{slug}' from project {id}.");
            return Results.Ok(new { project = id, removed = slug });
        });

        app.MapPost("/api/projects/{id:guid}/tasks", (Guid id, TaskRequest? request, HttpContext context, IRuntimeStore store) =>
        {
            var me = Acting(context, store);
            var detail = store.ReadProject(me, id);
            if (detail is null || !ProjectRules.MayLead(me, detail.Project, store))
            {
                return Results.NotFound(NoSuchProject);
            }

            var title = (request?.Title ?? "").Trim();
            var assignee = (request?.Assignee ?? "").Trim();
            if (title.Length == 0)
            {
                return Results.BadRequest(new { error = "A task needs a title." });
            }

            if (!detail.Members.Any(member => string.Equals(member.MemberSlug, assignee, StringComparison.Ordinal)))
            {
                return Results.BadRequest(new { error = "That person is not on this project." });
            }

            var task = store.AddTask(id, assignee, title);
            if (task is null)
            {
                return Results.NotFound(NoSuchProject);
            }

            Audit(store, context, "project.task.assign", $"'{me}' assigned task {task.Id} to '{assignee}' on project {id}.");
            return Results.Ok(new { task.Id, task.Title, assignee = task.AssigneeSlug, state = task.State.ToString() });
        });

        // The assignee reports. The lead cannot write this.
        //
        // That is the owner's decision about progress made structural rather than
        // promised: progress is what the member SAYS, so the manager physically
        // cannot author the member's word.
        app.MapPost("/api/projects/tasks/{id:guid}/report", (Guid id, ReportRequest? request, HttpContext context, IRuntimeStore store) =>
        {
            var me = Acting(context, store);
            var task = store.FindTask(id);
            if (task is null || !ProjectRules.MayReport(me, task))
            {
                return Results.NotFound(NoSuchProject);
            }

            if (!Enum.TryParse<TaskState>(request?.State ?? "", ignoreCase: true, out var state))
            {
                return Results.BadRequest(new { error = "State must be one of Todo, Doing, Blocked or Done." });
            }

            var report = store.Report(me, id, state, (request?.Text ?? "").Trim());
            if (report is null)
            {
                return Results.NotFound(NoSuchProject);
            }

            // The STATE is audited; the text is not. What somebody wrote about their
            // own work is the project's record to hold, not the machine's — the same
            // line direct messages draw, where the audit says a message was sent and
            // never what it said.
            Audit(store, context, "project.task.report", $"'{me}' reported task {id} as {state}.");
            return Results.Ok(new { report.Id, state = state.ToString(), report.CreatedAt });
        });
    }

    // The acting user is resolved from the caller, never from a parameter. For an
    // agent this is its owner; the agent has no membership of its own.
    private static string Acting(HttpContext context, IRuntimeStore store) =>
        ProjectRules.ActingUser((RuntimePrincipal)context.Items["principal"]!, store);

    private static void Audit(IRuntimeStore store, HttpContext context, string action, string detail)
    {
        var caller = (RuntimePrincipal)context.Items["principal"]!;
        store.AppendAudit(new AuditEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow, caller.Subject, null, action, AuditOutcome.Success, detail));
    }

    private static object Shape(ProjectDetail detail) => new
    {
        detail.Project.Id,
        detail.Project.Name,
        lead = detail.Project.LeadSlug,
        org = detail.Project.OrgSlug,
        detail.Project.CreatedAt,
        members = detail.Members.Select(member => member.MemberSlug),
        tasks = detail.Tasks.Select(task => new
        {
            task.Id,
            task.Title,
            assignee = task.AssigneeSlug,
            state = task.State.ToString(),
            task.Note,
            task.UpdatedAt
        })
    };
}

public sealed record CreateProjectRequest(string? Name);
public sealed record MemberRequest(string? Slug);
public sealed record TaskRequest(string? Title, string? Assignee);
public sealed record ReportRequest(string? State, string? Text);
