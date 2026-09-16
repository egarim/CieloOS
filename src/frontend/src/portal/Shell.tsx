import * as React from "react";
import { Bot, ClipboardList, Folder, HomeIcon, Languages, LogOut, Mail } from "lucide-react";
import type { Whoami } from "../shared/api";
import { LANGUAGES, useT, type Language } from "../shared/i18n";
import { Button } from "./ui/button";
import { NavItem } from "./ui/nav-item";
import { Chat } from "./views/Chat";
import { Files } from "./views/Files";
import { Messages } from "./views/Messages";
import { Projects } from "./views/Projects";
import { Home } from "./views/Home";

type PortalPlace = "home" | "chat" | "files" | "messages" | "projects";

const PLACES: {
  id: PortalPlace;
  labelKey: string;
  icon: React.ComponentType<{ className?: string }>;
  View: React.ComponentType<{ whoami: Whoami; onNavigate?: (place: PortalPlace) => void }>;
}[] = [
  { id: "home", labelKey: "portal.nav.home", icon: HomeIcon, View: Home },
  { id: "chat", labelKey: "portal.nav.chat", icon: Bot, View: Chat },
  { id: "files", labelKey: "portal.nav.files", icon: Folder, View: Files },
  { id: "messages", labelKey: "portal.nav.messages", icon: Mail, View: Messages },
  // Projects takes the fourth slot and Widgets moves INTO Chat, rendered above
  // the composer. By its own file's account a widget is "a job you ask for
  // often, kept as a button" — it is a chat shortcut, it lives in this
  // browser's localStorage with no server-side home, and it belongs where the
  // asking happens. Chat / Files / Messages / Projects is the better four for a
  // machine where a team works. Nothing was deleted: Widgets.tsx keeps its
  // logic and its test.
  { id: "projects", labelKey: "portal.nav.projects", icon: ClipboardList, View: Projects },
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
  const [active, setActive] = React.useState<PortalPlace>("home");
  const activePlace = PLACES.find((place) => place.id === active) ?? PLACES[0];
  const ActiveView = activePlace.View;

  return (
    <div className="min-h-dvh bg-slate-50 text-slate-900 dark:bg-slate-950 dark:text-slate-50">
      <header className="border-b border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
        <div className="mx-auto flex max-w-6xl items-center gap-3 px-4 py-3">
          <div className="min-w-0">
            <p className="hidden text-xs font-medium text-slate-500 sm:block dark:text-slate-400">
              {t("portal.navigation")}
            </p>
            <h1 className="truncate text-lg font-semibold sm:text-xl">{t("portal.title")}</h1>
          </div>

          <div className="ml-auto flex shrink-0 items-center gap-2 sm:gap-3">
            {/* Who you are is reassurance on a laptop and clutter on a phone, where
                it pushed the language picker and sign-out onto a second row. */}
            <p className="hidden min-w-0 truncate text-sm text-slate-600 lg:block dark:text-slate-300">
              {t("portal.user", { display: whoami.display })}
            </p>
            {/* Choosing a language is something you do once. It was a bordered
                box carrying the word "Language" AND the language name, at the same
                visual weight as the product title and sign-out, and on a phone it
                took about a third of the header — prominence out of all proportion
                to how often anyone touches it.

                Still a real <select>. The options are the native names, which is
                the part that matters: somebody who cannot read the current
                interface language has to be able to find their own. A custom
                popover would have looked tidier and been worse — the native
                control already has keyboard support, screen-reader semantics and
                the platform's own picker on a phone.

                So the select is kept and made invisible ON TOP of what you see: a
                globe alone at phone width, globe plus the language's own name from
                sm up. Focus is drawn on the wrapper, because a transparent control
                cannot show its own focus ring. */}
            <div className="relative flex items-center rounded-lg text-slate-600 transition-colors hover:bg-slate-100 focus-within:ring-2 focus-within:ring-slate-400 dark:text-slate-300 dark:hover:bg-slate-800">
              {/* min-w-11 as well as min-h-11: with the name hidden at phone width
                  this is an icon alone, and padding around a 16px glyph came to a
                  32px-wide target — under the 44px minimum, on the layout where
                  it is hardest to hit. From sm up the name makes it wider anyway. */}
              <span className="pointer-events-none flex min-h-11 min-w-11 items-center justify-center gap-2 px-2 text-sm sm:justify-start" aria-hidden="true">
                <Languages className="h-4 w-4" />
                <span className="hidden sm:inline">
                  {LANGUAGES.find((item) => item.code === language)?.native ?? language}
                </span>
              </span>
              <select
                aria-label={t("portal.language")}
                className="absolute inset-0 h-full w-full cursor-pointer opacity-0"
                value={language}
                onChange={(event) => onLanguageChange(event.target.value as Language)}
              >
                {LANGUAGES.map((item) => (
                  <option key={item.code} value={item.code}>
                    {item.native}
                  </option>
                ))}
              </select>
            </div>
            <Button variant="ghost" onClick={onSignOut} aria-label={t("portal.signOut")}>
              <LogOut className="h-4 w-4" aria-hidden="true" />
              <span className="hidden sm:inline">{t("portal.signOut")}</span>
            </Button>
          </div>
        </div>
      </header>

      {/* pb-24 on small screens keeps the last line of content clear of the fixed
          bottom bar. Without it the composer sits underneath the navigation and
          cannot be reached. */}
      <div className="mx-auto flex w-full max-w-6xl flex-col gap-4 p-4 pb-24 md:flex-row md:pb-4">
        {/* Desktop: a rail beside the work. */}
        <nav
          className="hidden md:flex md:w-56 md:shrink-0 md:flex-col md:gap-2"
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
          <ActiveView whoami={whoami} onNavigate={setActive} />
        </main>
      </div>

      {/* Mobile: a bottom bar, where a thumb is. A phone is not a narrow desktop,
          and navigation stacked at the top of a scrolling page is out of reach by
          the time you need it.

          env(safe-area-inset-bottom) keeps it clear of the iOS home indicator;
          without it the last row of targets sits under the gesture bar. */}
      {/* A distinct accessible name from the sidebar nav above. Both were
          labelled "Portal", so a screen reader announced two navigation landmarks
          with the same name and no way to tell them apart — while only one of them
          is ever on screen. */}
      <nav
        aria-label={t("portal.navigationBar")}
        className="fixed inset-x-0 bottom-0 z-40 grid border-t border-slate-200 bg-white/95 backdrop-blur md:hidden dark:border-slate-800 dark:bg-slate-900/95"
        // The column count is derived, not written as grid-cols-4. With the count
        // hard-coded, a fifth place wrapped onto a second row sitting OVER the
        // content — silently, on phones only. And it cannot be an interpolated
        // `grid-cols-${n}` class either: Tailwind scans source text for class
        // names, finds nothing for a computed one, and emits no rule at all, so
        // the bar would collapse to a single column.
        style={{
          gridTemplateColumns: `repeat(${PLACES.length}, minmax(0, 1fr))`,
          paddingBottom: "env(safe-area-inset-bottom)",
        }}
      >
        {PLACES.map((place) => {
          const Icon = place.icon;
          const isActive = place.id === active;
          return (
            <button
              key={place.id}
              type="button"
              onClick={() => setActive(place.id)}
              aria-current={isActive ? "page" : undefined}
              className={
                "relative flex min-h-16 flex-col items-center justify-center gap-1 px-1 text-xs font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-slate-500 " +
                (isActive
                  ? "text-slate-900 dark:text-slate-50"
                  : "text-slate-500 dark:text-slate-400")
              }
            >
              <Icon className="h-5 w-5 shrink-0" aria-hidden="true" />
              {/* Russian runs ~30% longer; the label wraps to two short lines
                  rather than being clipped or forcing the bar wider. */}
              <span className="w-full text-center leading-tight break-words">{t(place.labelKey)}</span>
              {isActive ? (
                <span aria-hidden="true" className="absolute top-0 h-0.5 w-10 rounded-full bg-slate-900 dark:bg-slate-50" />
              ) : null}
            </button>
          );
        })}
      </nav>
    </div>
  );
}
