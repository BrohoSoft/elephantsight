import clsx from "clsx";
import type { ReactNode } from "react";
import { NavLink } from "react-router";

/** Schede come link: ogni scheda ha il suo indirizzo, e si può aprire o condividere direttamente. */
export function Tabs({ items }: { items: { to: string; label: ReactNode; end?: boolean }[] }) {
  return (
    <nav className="mb-6 flex gap-1 overflow-x-auto border-b border-line">
      {items.map((item) => (
        <NavLink
          key={item.to}
          to={item.to}
          end={item.end}
          className={({ isActive }) =>
            clsx(
              "-mb-px border-b-2 px-3 py-2 text-[13px] whitespace-nowrap transition-colors",
              isActive ? "border-brand text-fg" : "border-transparent text-muted hover:text-fg",
            )
          }
        >
          {item.label}
        </NavLink>
      ))}
    </nav>
  );
}
