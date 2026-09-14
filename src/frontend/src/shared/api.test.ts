import { afterEach, expect, test, vi } from "vitest";
import {
  ApiError,
  UnauthorizedError,
  api,
  authHeaders,
  clearToken,
  readToken,
  writeToken,
} from "./api";

// A browser with site data blocked throws on every localStorage call. That is not
// exotic — private windows, "block third-party cookies and site data", and managed
// corporate profiles all do it, and the failure lands on the sign-in screen, which
// is the one screen a person cannot get past in order to report it.
//
// These tests exist because the first version of the storage guard swallowed the
// error and left a comment claiming sign-in still worked. It did not: authHeaders()
// re-reads storage, so the request went out with no Authorization header at all and
// the runtime answered 401. The bug was invisible on any developer's machine.

function withStorage(impl: Partial<Storage>) {
  Object.defineProperty(window, "localStorage", {
    configurable: true,
    value: impl as Storage,
  });
}

const blocked: Partial<Storage> = {
  getItem() {
    throw new DOMException("The operation is insecure.", "SecurityError");
  },
  setItem() {
    throw new DOMException("The operation is insecure.", "SecurityError");
  },
  removeItem() {
    throw new DOMException("The operation is insecure.", "SecurityError");
  },
};

afterEach(() => {
  clearToken();
  vi.unstubAllGlobals();
});

test("a token survives a browser that refuses to store it", () => {
  withStorage(blocked);
  clearToken();

  writeToken("identity-token-value");

  // The point of the whole exercise: the next request must still carry it.
  expect(readToken()).toBe("identity-token-value");
  expect(authHeaders().Authorization).toBe("Bearer identity-token-value");
});

test("signing out forgets the token even when storage throws", () => {
  withStorage(blocked);
  writeToken("identity-token-value");

  clearToken();

  expect(readToken()).toBeNull();
  expect(authHeaders().Authorization).toBeUndefined();
});

test("stored tokens still win, so a reload keeps you signed in", () => {
  const cells = new Map<string, string>();
  withStorage({
    getItem: (key: string) => cells.get(key) ?? null,
    setItem: (key: string, value: string) => void cells.set(key, value),
    removeItem: (key: string) => void cells.delete(key),
  });
  clearToken();

  writeToken("durable-token");

  expect(cells.get("cielo.token")).toBe("durable-token");
  expect(readToken()).toBe("durable-token");
});

// The CSRF header is not optional and has been dropped before. Assert it is on
// every request, with or without a token.
test("the panel header is always present", () => {
  withStorage(blocked);
  clearToken();

  expect(authHeaders()["X-Cielo-Panel"]).toBe("1");
  writeToken("t");
  expect(authHeaders()["X-Cielo-Panel"]).toBe("1");
});

test("a non-2xx response is an ApiError carrying its status and parsed body", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue({
      ok: false,
      status: 409,
      text: async () => JSON.stringify({ error: "The workspace changed." }),
    }),
  );

  await expect(api("/api/approvals/1/approve", { method: "POST" })).rejects.toMatchObject({
    name: "ApiError",
    status: 409,
    body: { error: "The workspace changed." },
  });
});

test("a 401 response stays an UnauthorizedError", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue({
      ok: false,
      status: 401,
      text: async () => "The session token was rejected.",
    }),
  );

  await expect(api("/api/whoami")).rejects.toBeInstanceOf(UnauthorizedError);
});
