import { render, screen } from "@testing-library/react";
import { afterEach, expect, test, vi } from "vitest";
import { LanguageProvider } from "../../shared/i18n";
import { Widgets } from "./Widgets";
import type { Whoami } from "../../shared/api";

// Saved tasks live in localStorage, and localStorage throws outright in a private
// window, under "block site data", and on managed corporate profiles. The portal
// has already been bitten once by an unguarded storage call — it turned a wrong
// password into a dead sign-in screen — so the guards here get a test rather than
// a comment promising they work.

const whoami: Whoami = {
  slug: "dev-test",
  display: "Dev Test",
  kind: "Human",
  homes: ["dev-test", "dev-test-agent"],
  deskProfile: "dotnet",
  deskProfileLabel: ".NET developer",
};

function withStorage(impl: Partial<Storage>) {
  Object.defineProperty(window, "localStorage", { configurable: true, value: impl as Storage });
}

function renderWidgets() {
  return render(
    <LanguageProvider language="en">
      <Widgets whoami={whoami} />
    </LanguageProvider>,
  );
}

afterEach(() => {
  vi.restoreAllMocks();
});

test("a browser that refuses to store still renders the page", () => {
  withStorage({
    getItem() {
      throw new DOMException("The operation is insecure.", "SecurityError");
    },
    setItem() {
      throw new DOMException("The operation is insecure.", "SecurityError");
    },
    removeItem() {
      throw new DOMException("The operation is insecure.", "SecurityError");
    },
  });

  renderWidgets();

  // Not a white screen: the empty state, and the button to make a task.
  expect(screen.getByRole("heading", { name: "Widgets" })).toBeInTheDocument();
  expect(screen.getByRole("button", { name: /New task/i })).toBeInTheDocument();
});

test("saved tasks come back", () => {
  const cells = new Map<string, string>([
    [
      "cielo.portal.tasks.dev-test",
      JSON.stringify([{ id: "a", title: "Weekly report", prompt: "Summarise this week" }]),
    ],
  ]);
  withStorage({
    getItem: (key: string) => cells.get(key) ?? null,
    setItem: (key: string, value: string) => void cells.set(key, value),
    removeItem: (key: string) => void cells.delete(key),
  });

  renderWidgets();

  expect(screen.getByText("Weekly report")).toBeInTheDocument();
  expect(screen.getByRole("button", { name: /Run/i })).toBeInTheDocument();
});

// Stored JSON is not a contract. It can be from an older version of this code, or
// edited by hand in devtools. Rendering a malformed entry puts "undefined" on the
// screen at best; at worst it throws inside render and takes the page with it.
test("rubbish in storage is dropped, not rendered", () => {
  const payload = JSON.stringify([
    { id: "ok", title: "Real task", prompt: "Do the thing" },
    { id: "broken" },
    "not even an object",
    null,
  ]);
  withStorage({
    getItem: () => payload,
    setItem: () => undefined,
    removeItem: () => undefined,
  });

  renderWidgets();

  expect(screen.getByText("Real task")).toBeInTheDocument();
  // Exactly one task survived: one Run button, not four.
  expect(screen.getAllByRole("button", { name: /Run/i })).toHaveLength(1);
});

test("storage holding something that is not an array does not crash the page", () => {
  withStorage({
    getItem: () => "{\"not\":\"an array\"}",
    setItem: () => undefined,
    removeItem: () => undefined,
  });

  renderWidgets();

  expect(screen.getByRole("heading", { name: "Widgets" })).toBeInTheDocument();
  expect(screen.queryAllByRole("button", { name: /Run/i })).toHaveLength(0);
});
