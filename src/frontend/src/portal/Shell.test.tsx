import { readFileSync } from "node:fs";
import { join } from "node:path";
import { render, screen } from "@testing-library/react";
import { expect, test, vi } from "vitest";
import { LanguageProvider } from "../shared/i18n";
import { Shell } from "./Shell";
import type { Whoami } from "../shared/api";

// The phone navigation used to be `grid-cols-4` with four places in the list. Two
// numbers that had to agree and nothing making them: a fifth place wrapped onto a
// second row sitting over the content, on phones only, with nothing failing.
//
// It cannot be fixed with an interpolated `grid-cols-${n}` either — Tailwind scans
// source text for class names, finds nothing for a computed one, and emits no rule
// at all, so the bar would collapse to a single column. Hence an inline style, and
// hence this test.

vi.mock("./views/Chat", () => ({ Chat: () => <div>chat</div> }));
vi.mock("./views/Files", () => ({ Files: () => <div>files</div> }));
vi.mock("./views/Messages", () => ({ Messages: () => <div>messages</div> }));
vi.mock("./views/Projects", () => ({ Projects: () => <div>projects</div> }));

const whoami: Whoami = {
  slug: "dev-test",
  display: "Dev Test",
  kind: "Human",
  homes: ["dev-test", "dev-test-agent"],
  deskProfile: "dotnet",
  deskProfileLabel: ".NET developer",
};

function renderShell() {
  return render(
    <LanguageProvider language="en">
      <Shell whoami={whoami} language="en" onLanguageChange={() => {}} onSignOut={() => {}} />
    </LanguageProvider>,
  );
}

test("the phone nav has exactly as many columns as there are places", () => {
  renderShell();

  const nav = screen.getByLabelText("Sections");
  const columns = nav.style.gridTemplateColumns;

  // One `minmax(0, 1fr)` per destination in the bar.
  const buttons = nav.querySelectorAll("button").length;
  expect(buttons).toBeGreaterThan(0);
  expect(columns).toBe(`repeat(${buttons}, minmax(0, 1fr))`);

  // And no hard-coded count left behind to disagree with it later.
  expect(nav.className).not.toMatch(/grid-cols-\d/);
});

test("the iOS home indicator cannot sit on top of the last row of targets", () => {
  // Asserted against the source, not the DOM: jsdom does not understand env() and
  // drops the declaration entirely, so reading it back gives "" whether the line
  // is there or not — a DOM assertion here would pass for the wrong reason the day
  // somebody deleted it.
  const shell = readFileSync(join(process.cwd(), "src", "portal", "Shell.tsx"), "utf8");
  expect(shell).toContain('paddingBottom: "env(safe-area-inset-bottom)"');
});
