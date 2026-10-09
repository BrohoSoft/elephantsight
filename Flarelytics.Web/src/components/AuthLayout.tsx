import type { ReactNode } from "react";
import { Link } from "react-router";
import { Logo } from "./Logo";
import { ThemeSwitcher } from "./ThemeSwitcher";

/** Le pagine fuori dal pannello: accesso, registrazione, link delle email. */
export function AuthLayout({ title, subtitle, children, footer }: { title: string; subtitle?: ReactNode; children: ReactNode; footer?: ReactNode }) {
  return (
    <div className="relative flex min-h-full flex-col items-center justify-center px-4 py-12">
      <div className="absolute top-4 right-4">
        <ThemeSwitcher compact />
      </div>
      <div className="mb-10">
        <Logo size="lg" />
      </div>
      <div className="w-full max-w-sm">
        <h1 className="text-xl font-medium tracking-tight text-fg">{title}</h1>
        {subtitle && <p className="mt-1.5 text-[0.8125rem] text-muted">{subtitle}</p>}
        <div className="mt-6">{children}</div>
        {footer && <div className="mt-6 text-center text-[0.8125rem] text-muted">{footer}</div>}
      </div>
      <p className="mt-10 text-xs text-faint">
        <Link to="/privacy" className="hover:text-fg">Privacy Policy</Link> · <Link to="/terms" className="hover:text-fg">Terms of Service</Link>
      </p>
    </div>
  );
}
