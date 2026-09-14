import * as React from "react";
import { api, clearToken, UnauthorizedError, writeToken, type Whoami } from "../shared/api";
import { useT } from "../shared/i18n";
import { Button } from "./ui/button";

type SignInMode = "password" | "token";

export function SignIn({ onSignedIn }: { onSignedIn: (user: Whoami) => void }) {
  const t = useT();
  const [mode, setMode] = React.useState<SignInMode>("password");
  const [slug, setSlug] = React.useState("");
  const [password, setPassword] = React.useState("");
  const [token, setToken] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);

  async function submitPassword(event: React.FormEvent) {
    event.preventDefault();
    if (!slug.trim() || !password || busy) return;
    setBusy(true);
    setError(null);
    try {
      await api<{ slug?: string }>("/api/auth/login", {
        method: "POST",
        body: JSON.stringify({ slug: slug.trim().toLowerCase(), password }),
      });
      const user = await api<Whoami>("/api/whoami");
      onSignedIn(user);
    } catch (submitError) {
      setError(
        submitError instanceof UnauthorizedError
          ? t("portal.signin.badCredentials")
          : t("portal.signin.error"),
      );
    } finally {
      setBusy(false);
    }
  }

  async function submitToken(event: React.FormEvent) {
    event.preventDefault();
    if (!token.trim() || busy) return;
    setBusy(true);
    setError(null);
    writeToken(token.trim());
    try {
      const user = await api<Whoami>("/api/whoami");
      setToken("");
      onSignedIn(user);
    } catch (submitError) {
      clearToken();
      setError(
        submitError instanceof UnauthorizedError
          ? t("portal.signin.tokenRejected")
          : t("portal.signin.error"),
      );
    } finally {
      setBusy(false);
    }
  }

  const inputClass =
    "min-h-11 w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-base text-slate-950 outline-none focus:border-slate-500 focus:ring-2 focus:ring-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-slate-50 dark:focus:border-slate-400 dark:focus:ring-slate-700";

  return (
    <main className="grid min-h-dvh place-items-center bg-slate-50 p-4 text-slate-900 dark:bg-slate-950 dark:text-slate-50">
      <div className="w-full max-w-md rounded-xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900">
        <p className="text-sm font-medium text-slate-500 dark:text-slate-400">
          CieloOS
        </p>
        <h1 className="mt-1 text-2xl font-semibold">{t("portal.signin.title")}</h1>
        <p className="mt-2 text-sm leading-6 text-slate-600 dark:text-slate-300">
          {t("portal.signin.hint")}
        </p>

        {mode === "password" ? (
          <form className="mt-6 space-y-4" onSubmit={submitPassword}>
            <label className="block">
              <span className="mb-1 block text-sm font-medium">{t("portal.signin.desk")}</span>
              <input
                className={inputClass}
                autoComplete="username"
                value={slug}
                onChange={(event) => setSlug(event.target.value)}
              />
            </label>
            <label className="block">
              <span className="mb-1 block text-sm font-medium">{t("portal.signin.password")}</span>
              <input
                className={inputClass}
                type="password"
                autoComplete="current-password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
              />
            </label>
            <Button className="w-full" type="submit" disabled={busy}>
              {t("portal.signin.submit")}
            </Button>
          </form>
        ) : (
          <form className="mt-6 space-y-4" onSubmit={submitToken}>
            <label className="block">
              <span className="mb-1 block text-sm font-medium">{t("portal.signin.token")}</span>
              <input
                className={inputClass}
                type="password"
                autoComplete="off"
                value={token}
                onChange={(event) => setToken(event.target.value)}
              />
            </label>
            <p className="text-sm leading-6 text-slate-600 dark:text-slate-300">
              {t("portal.signin.tokenHint")}
            </p>
            <Button className="w-full" type="submit" disabled={busy}>
              {t("portal.signin.tokenSubmit")}
            </Button>
          </form>
        )}

        <button
          className="mt-4 min-h-11 w-full rounded-lg px-3 py-2 text-left text-sm font-medium text-slate-600 hover:bg-slate-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-slate-500 dark:text-slate-300 dark:hover:bg-slate-800"
          type="button"
          onClick={() => {
            setError(null);
            setMode((current) => (current === "password" ? "token" : "password"));
          }}
        >
          {mode === "password"
            ? t("portal.signin.tokenFallback")
            : t("portal.signin.backToPassword")}
        </button>

        {error ? (
          <p className="mt-4 text-sm leading-6 text-red-600 dark:text-red-400" role="alert">
            {error}
          </p>
        ) : null}
      </div>
    </main>
  );
}
