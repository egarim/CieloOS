using System.Runtime.CompilerServices;

// The transport predicate is a pure function of the connection and the parsed
// terminator list, so it is testable without a WebApplicationFactory. The
// alternative — a real Kestrel with a real TLS handshake — is a much larger
// test for the same assertion.
[assembly: InternalsVisibleTo("WorkspaceRuntime.Tests")]
