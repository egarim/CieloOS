import * as React from "react";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, expect, test, vi } from "vitest";
import type { ApprovalView } from "../shared/api";
import { LanguageProvider } from "../shared/i18n";
import { PermissionApprovals } from "./PermissionApprovals";

function jsonResponse(body: unknown) {
  return {
    ok: true,
    status: 200,
    json: async () => body,
  } as Response;
}

function errorResponse(status: number, body: unknown) {
  return {
    ok: false,
    status,
    text: async () => JSON.stringify(body),
  } as Response;
}

function approval(overrides: Partial<ApprovalView> = {}): ApprovalView {
  return {
    id: "approval-1",
    toolRequestId: "request-1",
    userId: "user-1",
    status: "Pending",
    createdAt: "2026-09-14T10:00:00.000Z",
    resolvedAt: null,
    requestHash: "hash-1",
    reason: "Engineering policy rationale.",
    pendingRequest: {
      toolName: "browser",
      operation: "navigate",
      arguments: { id: "session-1", url: "https://example.com/path" },
    },
    preview: {
      supported: true,
      summary: "Open example.com.",
      changes: [{ address: "Browser address", before: null, after: "https://example.com/path" }],
    },
    title: "Open a page",
    surfaceName: "Agent Browser",
    reversible: false,
    policyReason: "Navigation IS the egress decision.",
    ...overrides,
  };
}

const fetchMock = vi.fn();

beforeEach(() => {
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function renderQueue() {
  return render(
    <LanguageProvider language="en">
      <PermissionApprovals />
    </LanguageProvider>,
  );
}

function pressEnter(element: HTMLElement) {
  const event = new KeyboardEvent("keydown", {
    key: "Enter",
    code: "Enter",
    bubbles: true,
    cancelable: true,
  });
  element.dispatchEvent(event);
  if (!event.defaultPrevented) {
    element.click();
  }
}

test("reversible null says we cannot tell, never that it cannot be undone", async () => {
  fetchMock.mockResolvedValueOnce(jsonResponse([approval({ reversible: null })]));

  renderQueue();

  await screen.findByRole("alertdialog");
  expect(screen.getByText("We cannot tell whether this can be undone.")).toBeInTheDocument();
  expect(screen.queryByText(/cannot be undone/i)).toBeNull();
});

test("the policy rationale is not in the DOM by default", async () => {
  const rationale = "Navigation IS the egress decision: the destination may be attacker text.";
  fetchMock.mockResolvedValueOnce(
    jsonResponse([approval({ reason: rationale, policyReason: rationale })]),
  );

  renderQueue();

  await screen.findByRole("alertdialog");
  expect(screen.queryByText(/Navigation IS the egress decision/i)).toBeNull();
});

test("Enter does not approve, even when the allow button has focus", async () => {
  fetchMock.mockResolvedValueOnce(jsonResponse([approval()]));

  renderQueue();

  await screen.findByRole("alertdialog");
  const allow = screen.getByRole("button", { name: "Allow" });

  pressEnter(allow);

  expect(fetchMock).toHaveBeenCalledTimes(1);
  expect(fetchMock.mock.calls[0][1]?.method).toBeUndefined();
});

test("a 409 re-reads the approval and does not silently retry the decision", async () => {
  fetchMock
    .mockResolvedValueOnce(jsonResponse([approval({ id: "old", title: "Open old page" })]))
    .mockResolvedValueOnce(errorResponse(409, { error: "The workspace changed." }))
    .mockResolvedValueOnce(
      jsonResponse([approval({ id: "new", title: "Open new page" })]),
    );

  renderQueue();

  await screen.findByRole("alertdialog");
  fireEvent.click(screen.getByRole("button", { name: "Allow" }));

  await screen.findByText(/open new page/i);
  expect(screen.getByRole("alertdialog")).toBeInTheDocument();
  expect(screen.getByRole("alert")).toHaveTextContent(
    "This request changed while you were deciding.",
  );

  const postCalls = fetchMock.mock.calls.filter(
    ([, init]) => (init as RequestInit | undefined)?.method === "POST",
  );
  expect(postCalls).toHaveLength(1);
});
