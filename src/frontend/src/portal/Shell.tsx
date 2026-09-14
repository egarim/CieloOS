import * as React from "react";
import { Folder, LayoutGrid, LogOut, Mail, MessageCircle } from "lucide-react";
import type { Whoami } from "../shared/api";
import { LANGUAGES, useT, type Language } from "../shared/i18n";
import { Button } from "./ui/button";
import { NavItem } from "./ui/nav-item";
import { Chat } from "./views/Chat";
import { Files } from "./views/Files";
import { Messages } from "./views/Messages";
import { Widgets } from "./views/Widgets";

type PortalPlace = "chat" | "files" | "messages" | "widgets";

const PLACES: {
  id: PortalPlace;
  labelKey: string;
  icon: React.ComponentType<{ className?: string }>;
  View: React.ComponentType;
}[] = [
  { id: "chat", labelKey: "portal.nav.chat", icon: MessageCircle, View: Chat },
  { id: "files", labelKey: "portal.nav.files", icon: Folder, View: Files },
  { id: "messages", labelKey: "portal.nav.messages", icon: Mail, View: Messages },
  { id: "widgets", labelKey: "portal.nav.widgets", icon: LayoutGrid, View: Widgets },
];

export function Shell({
  whoami,
  language,
  onLanguageChange,
  onSignOut,
}: {
  whoami: Whoami;
  language: Language;
  onLanguageChange: (next: Language) => void;
  onSignOut: () => void;
}) {
  const t = useT();
  const [active, setActive] = React.useState<PortalPlace>("chat");
  const activePlace = PLACES.find((place) => place.id === active) ?? PLACES[0];
  const ActiveView = activePlace.View;

  return (
    <div className="min-h-dvh bg-slate-50 text-slate-900 dark:bg-slate-950 dark:text-slate-50">
      <header className="border-b border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
        <div className="mx-auto flex max-w-6xl flex-wrap items-center gap-3 px-4 py-3">
          <div className="min-w-0">
            <p className="text-xs font-medium text-slate-500 dark:text-slate-400">
              {t("portal.navigation")}
            </p>
            <h1 className="break-words text-xl font-semibold">{t("portal.title")}</h1>
          </div>

          <div className="ml-auto flex flex-wrap items-center gap-3">
            <p className="min-w-0 break-words text-sm text-slate-600 dark:text-slate-300">
              {t("portal.user", { display: whoami.display })}
            </p>
            <label className="flex min-h-11 items-center gap-2 text-sm">
              <span>{t("portal.language")}</span>
              <select
                className="min-h-11 rounded-lg border border-slate-300 bg-white px-2 py-1 text-slate-950 outline-none focus:border-slate-500 focus:ring-2 focus:ring-slate-300 dark:border-slate-700 dark:bg-slate-950 dark:text-slate-50"
                value={language}
                onChange={(event) => onLanguageChange(event.target.value as Language)}
              >
                {LANGUAGES.map((item) => (
                  <option key={item.code} value={item.code}>
                    {item.native}
                  </option>
                ))}
              </select>
            </label>
            <Button variant="ghost" onClick={onSignOut}>
              <LogOut className="h-4 w-4" aria-hidden="true" />
              {t("portal.signOut")}
            </Button>
          </div>
        </div>
      </header>

      <div className="mx-auto flex w-full max-w-6xl flex-col gap-4 p-4 md:flex-row">
        <nav
          className="grid grid-cols-2 gap-2 md:flex md:w-56 md:shrink-0 md:flex-col"
          aria-label={t("portal.navigation")}
        >
          {PLACES.map((place) => {
            const Icon = place.icon;
            return (
              <NavItem
                key={place.id}
                active={place.id === active}
                icon={<Icon className="h-5 w-5" aria-hidden="true" />}
                onClick={() => setActive(place.id)}
              >
                {t(place.labelKey)}
              </NavItem>
            );
          })}
        </nav>

        <main className="min-w-0 flex-1 rounded-xl border border-slate-200 bg-white p-4 md:p-6 dark:border-slate-800 dark:bg-slate-900">
          <ActiveView />
        </main>
      </div>
    </div>
  );
}
