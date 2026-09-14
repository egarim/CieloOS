import { useT } from "../../shared/i18n";

export function Messages() {
  const t = useT();
  return (
    <section>
      <h2 className="text-2xl font-semibold">{t("portal.nav.messages")}</h2>
      <p className="mt-2 max-w-prose text-sm leading-6 text-slate-600 dark:text-slate-300">
        {t("portal.view.messages")}
      </p>
    </section>
  );
}
