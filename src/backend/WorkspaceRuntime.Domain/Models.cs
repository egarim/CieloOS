namespace WorkspaceRuntime.Domain;

public enum PolicyDecision
{
    Allow,
    Deny,
    RequireApproval
}

public enum ApprovalStatus
{
    Pending,
    Approved,
    Rejected
}

public enum AuditOutcome
{
    Success,
    Blocked,
    PendingApproval
}

// Slug is the stable lowercase identity key (^[a-z0-9-]{1,32}$): the home
// volume name, the audit attribution, and the bearer-token subject all key off
// it. A human's slug is their own; an agent's slug is its identity.
// DeskProfile is what the desk is FOR (see DeskProfiles): it decides the session
// image, the agent's tool grant and what the home starts with. Defaulted so every
// user created before profiles existed reads as the office desk they already had.
// Language is an attribute of the PERSON, not of the browser they happen to be
// using: it has to reach the panel, the locale and keyboard of their session, and
// the agent's prompt so it answers in their language. A browser setting reaches
// only the first of those. BCP-47, defaulted so every user created before this
// existed reads as English rather than as unset.
// OrgSlug is the organization this person belongs to, and it is the ONLY authority
// on that. New users are minted with it as a prefix — acme-maria, nova-maria — so
// two organizations can each have a Maria without one byte of the slug machinery
// changing: `acme-maria != nova-maria` is a string comparison Ownership already
// makes, so every home, session, screenshot and audit route gets organization
// isolation with no edit.
//
// But the prefix is a MINTING RULE, NOT A STRUCTURE. Nothing parses it, ever.
// `slug.Split('-')[0]` is a bug in the same family as handing a membership slug to
// CanAccessHome: it looks like it works, and it silently puts someone in the wrong
// organization the first time a person's name begins with another org's slug. It
// is also why the two users who predate organizations keep their bare slugs and
// simply carry the OrgSlug of the organization they are in — a person's prefix
// records how they were minted, not where they belong.
//
// Neither OrgSlug nor IsMachineOwner has a default, deliberately. A defaulted
// OrgSlug means a construction site that forgets to stamp one does not leave the
// user in no organization — it enrols them in whichever organization "" happens to
// be, which is the founder's. Every construction site is a compile error until it
// decides.
public sealed record PlatformUser(
    Guid Id,
    string DisplayName,
    string Email,
    string Slug,
    string OrgSlug,
    bool IsMachineOwner,
    string DeskProfile = "office",
    string Language = "en");

// An organization on this machine. A name and a slug, and nothing else — it owns
// no files, no volume and no session. Membership lives on PlatformUser.OrgSlug.
public sealed record Organization(Guid Id, string Slug, string DisplayName, DateTimeOffset CreatedAt);

public sealed record Workspace(Guid Id, Guid OwnerUserId, string Name);

public sealed record AgentProfile(
    Guid Id,
    Guid OwnerUserId,
    Guid WorkspaceId,
    string Name,
    string InferenceProvider,
    IReadOnlySet<string> GrantedTools,
    string Slug);

// A resolved caller identity: a human user or an agent, with the slug that
// keys its home, tokens, and audit attribution.
public enum PrincipalKind
{
    Human,
    Agent
}

public sealed record RuntimePrincipal(PrincipalKind Kind, Guid Subject, string Slug, string Display);

public sealed record ToolRequest(
    Guid Id,
    Guid UserId,
    Guid AgentId,
    string ToolName,
    string Operation,
    IReadOnlyDictionary<string, string> Arguments,
    DateTimeOffset CreatedAt);

public sealed record PolicyEvaluation(
    PolicyDecision Decision,
    string Reason,
    IReadOnlyDictionary<string, string> Evidence);

public sealed record ApprovalRecord(
    Guid Id,
    Guid ToolRequestId,
    Guid UserId,
    ApprovalStatus Status,
    string Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    string RequestHash = "");

// Principal is the actor that drove this event; OnBehalfOf is the identity
// whose grants/seat were used, when different (dual-actor: "joche, on behalf of
// joche-agent"). For an agent acting as itself, OnBehalfOf is null.
public sealed record AuditEvent(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? UserId,
    Guid? AgentId,
    string Action,
    AuditOutcome Outcome,
    string Detail,
    Guid? CorrelationId = null,
    string? Principal = null,
    string? OnBehalfOf = null,
    string? SessionId = null);

public sealed record SpreadsheetCell(string Address, string Value);

public sealed record SpreadsheetState(IReadOnlyDictionary<string, string> Cells);

public sealed record ToolExecutionResult(
    bool Executed,
    string Message,
    SpreadsheetState? Spreadsheet);

public sealed record CellChange(string Address, string? Before, string? After);

public sealed record EffectPreview(
    bool Supported,
    string Summary,
    IReadOnlyList<CellChange> Changes);

// A delegated piece of work. The thread is the unit the panel comes back to:
// one thing asked for, with its own history, approval context and artifacts.
public enum ThreadStatus
{
    Working,
    NeedsYou,
    Done,
    Failed,
    Abandoned
}

public enum ThreadMessageRole
{
    Person,
    Agent
}

public sealed record Thread(
    Guid Id,
    string OwnerSlug,
    string Title,
    ThreadStatus State,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt);

public sealed record ThreadMessage(
    Guid Id,
    Guid ThreadId,
    ThreadMessageRole Role,
    string Text,
    DateTimeOffset CreatedAt);

// A thread is persisted separately from its messages; reads that need both
// get this shape rather than a thread with a mutable message list.
public sealed record ThreadWithMessages(
    Thread Thread,
    IReadOnlyList<ThreadMessage> Messages);

// A message from one person on this machine to another.
//
// Deliberately NOT a Thread. A thread is delegated work — one owner and their
// agents, scoped by Ownership.CanAccessHome. A direct message crosses exactly the
// boundary that scoping exists to enforce, so it is a different thing with a
// different rule: both ends are people, and only those two may read it.
public sealed record DirectMessage(
    Guid Id,
    string FromSlug,
    string ToSlug,
    string Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

// One row per person you have talked to, for the list beside the conversation.
public sealed record Conversation(
    string WithSlug,
    string WithDisplay,
    string LastText,
    string LastFromSlug,
    DateTimeOffset LastAt,
    int Unread);

// The pair key. Ordered so (alice, bob) and (bob, alice) are the SAME
// conversation — without that, replying would start a second one and each person
// would see half the exchange.
public static class ConversationKey
{
    public static string For(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
}
