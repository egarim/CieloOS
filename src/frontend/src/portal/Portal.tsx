import * as React from "react";
import { api, clearToken, UnauthorizedError, type Whoami } from "../shared/api";
import {
  LanguageProvider,
  resolveLanguage,
  useT,
  type Language,
} from "../shared/i18n";
import { Button } from "./ui/button";
import { Shell } from "./Shell";
import { SignIn } from "./SignIn";
import { PermissionApprovals } from "./PermissionApprovals";

type PortalStatus = "checking" | "signed-out" | "signed-in" | "error";

export default function Portal() {
  const [status, setStatus] = React.useState<PortalStatus>("checking");
  const [whoami, setWhoami] = React.useState<Whoami | null>(null);
  const [language, setLanguage] = React.useState<Language>(() =>
    resolveLanguage(window.navigator.language),
  );

  // Screen readers pick a voice from this, and the browser picks hyphenation and
  // quotation rules from it. Left at the document's hard-coded "en", a Russian
  // portal is announced in an English voice — which is worse than no translation,
  // because it sounds like a mistake rather than a missing feature.
  React.useEffect(() => {
    document.documentElement.lang = language;
  }, [language]);

  React.useEffect(() => {
    let alive = true;

    async function checkSession() {
      try {
        const user = await api<Whoami>("/api/whoami");
        if (!alive) return;
        setWhoami(user);
        setStatus("signed-in");
        setLanguage(resolveLanguage(user.language));
      } catch (error) {
        if (!alive) return;
        setStatus(error instanceof UnauthorizedError ? "signed-out" : "error");
      }
    }

    checkSession();
    return () => {
      alive = false;
    };
  }, []);

  const handleSignedIn = React.useCallback((user: Whoami) => {
    setWhoami(user);
    setStatus("signed-in");
    setLanguage(resolveLanguage(user.language));
  }, []);

  const handleSignOut = React.useCallback(async () => {
    try {
      await api<{ signedOut?: boolean }>("/api/auth/logout", { method: "POST" });
    } catch {
      // Even if the runtime cannot be reached, this browser can stop showing the shell.
    }
    clearToken();
    setWhoami(null);
    setStatus("signed-out");
  }, []);

  // Each change fires its own request, and they can land out of order: pick
  // Russian then Spanish quickly and the Russian response may arrive last,
  // leaving the server on Russian while the screen shows Spanish. The preference
  // then silently reverts on the next sign-in. A counter is enough — only the
  // newest choice is allowed to be the one that was saved.
  const languageRequest = React.useRef(0);

  const handleLanguageChange = React.useCallback(
    async (next: Language) => {
      setLanguage(next);
      if (!whoami) return;
      const ticket = ++languageRequest.current;
      try {
        await api<{ language?: string; appliesToNewSessions?: boolean }>(
          "/api/auth/language",
          { method: "POST", body: JSON.stringify({ language: next }) },
        );
        if (ticket !== languageRequest.current) {
          // A newer choice was made while this was in flight; it owns the server
          // now, and re-sending this one would undo it.
          return;
        }
      } catch {
        // Keep the visible language usable even if the preference could not be saved.
      }
    },
    [whoami],
  );

  return (
    <LanguageProvider language={language}>
      <PortalContent
        status={status}
        whoami={whoami}
        language={language}
        onSignedIn={handleSignedIn}
        onSignOut={handleSignOut}
        onLanguageChange={handleLanguageChange}
      />
    </LanguageProvider>
  );
}

function PortalContent({
  status,
  whoami,
  language,
  onSignedIn,
  onSignOut,
  onLanguageChange,
}: {
  status: PortalStatus;
  whoami: Whoami | null;
  language: Language;
  onSignedIn: (user: Whoami) => void;
  onSignOut: () => void;
  onLanguageChange: (next: Language) => void;
}) {
  const t = useT();

  if (status === "checking") {
    return (
      <div className="grid min-h-dvh place-items-center bg-slate-50 p-4 text-slate-900 dark:bg-slate-950 dark:text-slate-50">
        <p role="status">{t("portal.loading")}</p>
      </div>
    );
  }

  if (status === "error") {
    return (
      <div className="grid min-h-dvh place-items-center bg-slate-50 p-4 text-slate-900 dark:bg-slate-950 dark:text-slate-50">
        <div className="w-full max-w-md rounded-xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900">
          <h1 className="text-xl font-semibold">{t("portal.error.title")}</h1>
          <p className="mt-2 text-sm leading-6 text-slate-600 dark:text-slate-300">
            {t("portal.error.hint")}
          </p>
          <Button className="mt-4" onClick={() => window.location.reload()}>
            {t("portal.error.retry")}
          </Button>
        </div>
      </div>
    );
  }

  if (status === "signed-out" || !whoami) {
    return <SignIn onSignedIn={onSignedIn} />;
  }

  return (
    <>
      <Shell
        whoami={whoami}
        language={language}
        onLanguageChange={onLanguageChange}
        onSignOut={onSignOut}
      />
      <PermissionApprovals />
    </>
  );
}
