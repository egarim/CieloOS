import * as React from "react";
import { cn } from "../lib/utils";

type ButtonProps = React.ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: "primary" | "ghost";
};

export function Button({ className, variant = "primary", ...props }: ButtonProps) {
  return (
    <button
      className={cn(
        "inline-flex min-h-11 items-center justify-center gap-2 rounded-lg px-4 py-2 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-slate-500 disabled:pointer-events-none disabled:opacity-50",
        variant === "primary" &&
          "bg-slate-900 text-white hover:bg-slate-700 dark:bg-slate-100 dark:text-slate-950 dark:hover:bg-white",
        variant === "ghost" &&
          "bg-transparent text-slate-700 hover:bg-slate-200 dark:text-slate-200 dark:hover:bg-slate-800",
        className,
      )}
      {...props}
    />
  );
}
