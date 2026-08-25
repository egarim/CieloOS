import * as React from "react";
import en from "../i18n/en.json";
import ru from "../i18n/ru.json";
import es from "../i18n/es.json";

// The desktop is bilingual from its first line rather than translated afterwards.
//
// Retrofitting a dictionary into the panel this replaces would have been thrown
// away twice: once when the panel goes, and once more because ten example
// documents tell people to click "Examples" and "Run", which is only true while
// those labels are hardcoded English. Doing it here means those documents and
// these labels change together.
//
// Language comes from the USER, not the browser: it also decides the locale and
// keyboard of their session and what language the agent answers in, and a browser
// setting reaches none of that. The browser is only consulted before anyone has
// signed in, when there is no user to ask.

export type Language = "en" | "ru" | "es";

const CATALOGUES: Record<Language, Record<string, string>> = {
  en: en as Record<string, string>,
  ru: ru as Record<string, string>,
  es: es as Record<string, string>,
};

export const LANGUAGES: { code: Language; native: string }[] = [
  { code: "en", native: "English" },
  { code: "ru", native: "Русский" },
  { code: "es", native: "Español" },
];

export function resolveLanguage(code: string | null | undefined): Language {
  if (!code) return "en";
  // A regional variant lands where it means to: ru-RU and es-419 must not fall
  // through to English just because the exact tag is not on the list.
  const primary = code.split(/[-_]/)[0].toLowerCase();
  return primary === "ru" || primary === "es" ? primary : "en";
}

type Translate = (key: string, values?: Record<string, string | number>) => string;

const Context = React.createContext<{ language: Language; t: Translate }>({
  language: "en",
  t: (key) => (en as Record<string, string>)[key] ?? key,
});

export function LanguageProvider({
  language,
  children,
}: {
  language: Language;
  children: React.ReactNode;
}) {
  const value = React.useMemo(() => {
    const catalogue = CATALOGUES[language] ?? CATALOGUES.en;
    const t: Translate = (key, values) => {
      // Fall back through English to the key itself. A missing entry shows the
      // English rather than an empty space or a raw identifier, because a
      // half-translated interface is confusing and a blank one is broken.
      const template = catalogue[key] ?? CATALOGUES.en[key];
      if (template === undefined) {
        if (import.meta.env?.DEV) {
          console.warn(`[i18n] no string for "${key}"`);
        }
        return key;
      }
      if (!values) return template;
      return template.replace(/\{(\w+)\}/g, (whole, name) =>
        Object.prototype.hasOwnProperty.call(values, name) ? String(values[name]) : whole);
    };
    return { language, t };
  }, [language]);

  return <Context.Provider value={value}>{children}</Context.Provider>;
}

export const useT = () => React.useContext(Context).t;
export const useLanguage = () => React.useContext(Context).language;
