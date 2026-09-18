import * as React from "react";
import { api, ApiError, clearToken, type Whoami } from "../shared/api";
import { useT } from "../shared/i18n";
import { Button } from "./ui/button";

type InviteState = "live" | "used" | "revoked" | "superseded" | "expired";

type InvitePreview = {
  state: InviteState;
  slug?: string;
  displayName?: string;
  orgDisplayName?: string;
  invitedBy?: string;
  expiresAt?: string;
};

const deadStates = new Set<InviteState>(["used", "revoked", "superseded", "expired"]);

export function RedeemInvite({ code, onSignedIn }: { code: string; onSignedIn: (user: Whoami) => void }) {
  const t = useT();
  const [preview, setPreview] = React.useState<InvitePreview | null>(null);
  const [loading, setLoading] = React.useState(true);
  const [password, setPassword] = React.useState("");
  const [confirmation, setConfirmation] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);

  React.useEffect(() => {
    let alive = true;
    setLoading(true);
    api<InvitePreview>("/api/invites/preview", {
      method: "POST",
      body: JSON.stringify({ code }),
    })
      .then((result) => {
        if (alive) setPreview(result);
      })
      .catch(() => {
        if (alive) setError(t("portal.invite.previewError"));
      })
      .finally(() => {
        if (alive) setLoading(false);
      });
    return () => {
      alive = false;
    };
  }, [code, t]);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    if (busy) return;
    if (password.length < 10) {
      setError(t("portal.invite.passwordShort"));
      return;
    }
    if (password !== confirmation) {
      setError(t("portal.invite.passwordMismatch"));
      return;
    }

    setBusy(true);
    setError(null);
    try {
      await api("/api/invites/redeem", {
        method: "POST",
        body: JSON.stringify({ code, password }),
      });
      // The new cookie must win over an identity token left by the last person
      // using this browser when whoami decides which account has signed in.
      clearToken();
      window.history.replaceState(null, "", `${window.location.pathname}${window.location.search}`);
      const user = await api<Whoami>("/api/whoami");
      onSignedIn(user);
    } catch (submitError) {
      if (submitError instanceof ApiError) {
        const body = submitError.body as { state?: InviteState } | null;
        if (body?.state && deadStates.has(body.state)) {
          setPreview({ state: body.state });
          return;
        }
      }
      setError(t("portal.invite.redeemError"));
    } finally {
      setBusy(false);
    }
  }

  const inputClass =
    "min-h-11 w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-base text-slate-950 outline-none focus:border-slate-500 focus:ring-2 focus:ring-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-slate-50 dark:focus:border-slate-400 dark:focus:ring-slate-700";

  return (
    <main className="grid min-h-dvh place-items-center bg-slate-50 p-4 text-slate-900 dark:bg-slate-950 dark:text-slate-50">
      <div className="w-full max-w-md rounded-xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900">
        <p className="text-sm font-medium text-slate-500 dark:text-slate-400">CieloOS</p>
        <h1 className="mt-1 text-2xl font-semibold">{t("portal.invite.title")}</h1>

        {loading ? <p className="mt-4" role="status">{t("portal.invite.loading")}</p> : null}

        {!loading && preview?.state !== "live" && preview?.state ? (
          <div className="mt-4" role="alert">
            <h2 className="font-semibold">{t(`portal.invite.dead.${preview.state}.title`)}</h2>
            <p className="mt-2 text-sm leading-6 text-slate-600 dark:text-slate-300">
              {t(`portal.invite.dead.${preview.state}.hint`)}
            </p>
          </div>
        ) : null}

        {!loading && preview?.state === "live" ? (
          <>
            <p className="mt-4 text-sm leading-6 text-slate-600 dark:text-slate-300">
              {t("portal.invite.identity", {
                display: preview.displayName ?? "",
                organization: preview.orgDisplayName ?? "",
              })}
            </p>
            <form className="mt-6 space-y-4" onSubmit={submit}>
              <p className="text-sm leading-6 text-slate-600 dark:text-slate-300">
                {t("portal.invite.passwordRule")}
              </p>
              <label className="block">
                <span className="mb-1 block text-sm font-medium">{t("portal.invite.password")}</span>
                <input className={inputClass} type="password" autoComplete="new-password" value={password} onChange={(event) => setPassword(event.target.value)} />
              </label>
              <label className="block">
                <span className="mb-1 block text-sm font-medium">{t("portal.invite.confirm")}</span>
                <input className={inputClass} type="password" autoComplete="new-password" value={confirmation} onChange={(event) => setConfirmation(event.target.value)} />
              </label>
              <Button className="w-full" type="submit" disabled={busy}>
                {busy ? t("portal.invite.submitting") : t("portal.invite.submit")}
              </Button>
            </form>
          </>
        ) : null}

        {error ? <p className="mt-4 text-sm leading-6 text-red-600 dark:text-red-400" role="alert">{error}</p> : null}
      </div>
    </main>
  );
}
