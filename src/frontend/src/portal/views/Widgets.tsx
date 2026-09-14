import { useT } from "../../shared/i18n";

export function Widgets() {
  const t = useT();
  return (
    <section>
      <h2 className="text-2xl font-semibold">{t("portal.nav.widgets")}</h2>
      <p className="mt-2 max-w-prose text-sm leading-6 text-slate-600 dark:text-slate-300">
        {t("portal.view.widgets")}
      </p>
    </section>
  );
}
