import * as React from "react";
import { cn } from "../lib/utils";

type NavItemProps = React.ButtonHTMLAttributes<HTMLButtonElement> & {
  active?: boolean;
  icon?: React.ReactNode;
};

export function NavItem({ active = false, icon, className, children, ...props }: NavItemProps) {
  return (
    <button
      aria-current={active ? "page" : undefined}
      className={cn(
        "flex min-h-11 w-full items-center gap-3 rounded-lg px-3 py-2 text-left text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-slate-500",
        active
          ? "bg-slate-900 text-white dark:bg-slate-100 dark:text-slate-950"
          : "text-slate-700 hover:bg-slate-200 dark:text-slate-200 dark:hover:bg-slate-800",
        className,
      )}
      {...props}
    >
      {icon ? <span className="shrink-0" aria-hidden="true">{icon}</span> : null}
      <span className="min-w-0 break-words text-left">{children}</span>
    </button>
  );
}
