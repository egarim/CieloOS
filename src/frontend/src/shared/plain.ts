// A filter, not a translator.
//
// The runtime answers in its own vocabulary. Some of those answers are already a
// sentence a person can read ("'joche' may not open a screen over 'agent-ana'.")
// and some are engineering prose written for whoever is reading the source
// ("No executor is registered for surface 'session'.", "This command requires the
// human principal.", "Revision mismatch: the surface changed since it was
// observed."). Both arrive through the same field, so a view that shows the
// server's text verbatim will show the second kind too — and the desktop is not
// allowed to say those words.
//
// This does not attempt to rewrite them. It only recognises that a line is
// written for the machine, so the caller can put its own plain line there
// instead. Guessing at a translation would be worse than admitting the reason
// came back unreadable.

const MACHINE_WORDS =
  /home volume|desk profile|\bsurface(s)?\b|\bprincipal(s)?\b|\bpolic(y|ies)\b|\bmanifest\b|\bexecutor\b|require ?approval|\bdry ?run\b/i;

export const carriesMachineWords = (text: string): boolean => MACHINE_WORDS.test(text);

// The runtime answers a failure as JSON `{ error }` when it can and as plain text
// when it cannot. Both arrive here as an Error message, so unwrap once.
export function serverText(problem: unknown): string {
  const raw = (problem instanceof Error ? problem.message : String(problem ?? "")).trim();
  if (raw.startsWith("{")) {
    try {
      const body = JSON.parse(raw) as { error?: unknown };
      if (typeof body.error === "string" && body.error.trim()) return body.error.trim();
    } catch {
      // Not JSON after all — keep what actually came back.
    }
  }
  return raw;
}

// What to show a person: the server's own words when they are readable, and the
// caller's fallback line when they are not. Long enough to be a paragraph of
// reasoning is also a reason to withhold it — that is never the sentence someone
// standing at a failed button needs.
export function readable(text: string, fallback: string, limit = 220): string {
  const trimmed = text.trim();
  if (!trimmed || carriesMachineWords(trimmed)) return fallback;
  return trimmed.length > limit ? `${trimmed.slice(0, limit - 1)}…` : trimmed;
}
