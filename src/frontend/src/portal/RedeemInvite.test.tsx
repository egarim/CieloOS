import * as React from "react";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, test, vi } from "vitest";
import { clearToken, writeToken } from "../shared/api";
import { LanguageProvider } from "../shared/i18n";
import Portal from "./Portal";
import { RedeemInvite } from "./RedeemInvite";

const response = (body: unknown, status = 200) =>
  Promise.resolve(new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  }));

function renderInvite(code = "invite-code", onSignedIn = vi.fn()) {
  return render(
    <LanguageProvider language="en">
      <RedeemInvite code={code} onSignedIn={onSignedIn} />
    </LanguageProvider>,
  );
}

beforeEach(() => {
  clearToken();
  window.history.replaceState(null, "", "/portal.html");
});

afterEach(() => {
  vi.unstubAllGlobals();
  clearToken();
});

test("the invite hash wins over a stale stored token and the normal session check", async () => {
  writeToken("someone-elses-token");
  window.history.replaceState(null, "", "/portal.html#invite=fresh-code");
  const fetchMock = vi.fn(() => response({ state: "used" }));
  vi.stubGlobal("fetch", fetchMock);

  render(<Portal />);

  expect(await screen.findByText("This invitation has already been used")).toBeInTheDocument();
  expect(fetchMock).toHaveBeenCalledTimes(1);
  const calls = fetchMock.mock.calls as unknown as [RequestInfo | URL, RequestInit?][];
  expect(calls[0][0]).toBe("/api/invites/preview");
  expect(JSON.parse(String(calls[0][1]?.body))).toEqual({ code: "fresh-code" });
});

test("a dead invitation names its state and tells the person what to do", async () => {
  vi.stubGlobal("fetch", vi.fn(() => response({ state: "revoked" })));
  renderInvite();

  expect(await screen.findByText("This invitation was called off")).toBeInTheDocument();
  expect(screen.getByText("Ask the owner for a new invitation link.")).toBeInTheDocument();
});

test("an unknown code and an expired invitation render the same public state", async () => {
  const fetchMock = vi.fn(() => response({ state: "expired" }));
  vi.stubGlobal("fetch", fetchMock);

  const first = renderInvite("unknown-code");
  expect(await screen.findByText("This invitation is no longer available")).toBeInTheDocument();
  const unknownText = screen.getByRole("alert").textContent;
  first.unmount();

  renderInvite("expired-code");
  expect(await screen.findByText("This invitation is no longer available")).toBeInTheDocument();
  expect(screen.getByRole("alert").textContent).toBe(unknownText);
});

test("a short password is refused without calling redeem", async () => {
  const fetchMock = vi.fn(() => response({
    state: "live",
    displayName: "Мария",
    orgDisplayName: "Cielo",
  }));
  vi.stubGlobal("fetch", fetchMock);
  renderInvite();

  expect(await screen.findByText("You are joining Cielo as Мария.")).toBeInTheDocument();
  const fields = screen.getAllByLabelText(/password/i);
  fireEvent.change(fields[0], { target: { value: "short" } });
  fireEvent.change(fields[1], { target: { value: "short" } });
  fireEvent.click(screen.getByRole("button", { name: "Accept invitation" }));

  expect(screen.getByRole("alert")).toHaveTextContent("at least 10 characters");
  expect(fetchMock).toHaveBeenCalledTimes(1);
});

test("success clears the stored identity token before loading the signed-in person", async () => {
  writeToken("old-identity");
  window.localStorage.setItem("runtime.token", "old-panel-identity");
  const onSignedIn = vi.fn();
  const fetchMock = vi.fn((path: RequestInfo | URL) => {
    if (path === "/api/invites/preview") {
      return response({ state: "live", displayName: "Maria", orgDisplayName: "Cielo" });
    }
    if (path === "/api/invites/redeem") return response({ slug: "maria", displayName: "Maria" });
    return response({
      slug: "maria", display: "Maria", kind: "Human", homes: ["maria"],
      deskProfile: "office", deskProfileLabel: "Office",
    });
  });
  vi.stubGlobal("fetch", fetchMock);
  window.history.replaceState(null, "", "/portal.html#invite=invite-code");
  renderInvite("invite-code", onSignedIn);

  await screen.findByText("You are joining Cielo as Maria.");
  const fields = screen.getAllByLabelText(/password/i);
  fireEvent.change(fields[0], { target: { value: "long-enough-password" } });
  fireEvent.change(fields[1], { target: { value: "long-enough-password" } });
  fireEvent.click(screen.getByRole("button", { name: "Accept invitation" }));

  await waitFor(() => expect(onSignedIn).toHaveBeenCalledOnce());
  expect(window.localStorage.getItem("cielo.token")).toBeNull();
  expect(window.localStorage.getItem("runtime.token")).toBeNull();
  expect(window.location.hash).toBe("");
  const calls = fetchMock.mock.calls as unknown as [RequestInfo | URL, RequestInit?][];
  const whoamiCall = calls.find(([path]) => path === "/api/whoami");
  expect((whoamiCall?.[1]?.headers as Record<string, string>).Authorization).toBeUndefined();
});
