import clsx from "clsx";

/**
 * Il segno di ElephantSight: un elefante visto di fronte, minimale. Orecchie
 * grandi, zanne, proboscide arricciata e due occhi ben visibili: l'elefante
 * che vede (e ricorda) tutto delle tue app.
 *
 * Senza riquadro intorno, nei colori del logo definiti in styles.css
 * (--logo-*), diversi fra tema chiaro e scuro.
 */
export function LogoMark({ className = "size-9" }: { className?: string }) {
  return (
    <svg viewBox="0 0 64 64" className={clsx("shrink-0", className)} aria-hidden>
      <path d="M24 17C13 10 3 17 4 30c1 11 10 17 21 13z" fill="var(--logo-ear)" />
      <path d="M40 17c11-7 21 0 20 13-1 11-10 17-21 13z" fill="var(--logo-ear)" />
      <ellipse cx="32" cy="25" rx="13" ry="15" fill="var(--logo-head)" />
      <path d="M26.5 35c-1.8 4-2.2 7.5-.6 10.6" fill="none" stroke="var(--logo-tusk)" strokeWidth="2.8" strokeLinecap="round" />
      <path d="M37.5 35c1.8 4 2.2 7.5.6 10.6" fill="none" stroke="var(--logo-tusk)" strokeWidth="2.8" strokeLinecap="round" />
      <path d="M32 33c0 8-.6 13.5 2.2 17.4 2.2 3 6.4 2.6 6.6-.6" fill="none" stroke="var(--logo-head)" strokeWidth="7.5" strokeLinecap="round" strokeLinejoin="round" />
      <circle cx="26.2" cy="23.5" r="2.3" fill="var(--logo-eye)" />
      <circle cx="37.8" cy="23.5" r="2.3" fill="var(--logo-eye)" />
      <circle cx="27" cy="22.7" r=".8" fill="#fff" />
      <circle cx="38.6" cy="22.7" r=".8" fill="#fff" />
    </svg>
  );
}

/** Il logo con il nome. "lg" per le pagine di accesso, dove è l'unica cosa da guardare. */
export function Logo({ withName = true, size = "md" }: { withName?: boolean; size?: "md" | "lg" }) {
  return (
    <span className={clsx("inline-flex items-center", size === "lg" ? "flex-col gap-3" : "gap-2")}>
      <LogoMark className={size === "lg" ? "size-24" : "size-9"} />
      {withName && (
        <span className={clsx("font-semibold tracking-tight text-fg", size === "lg" ? "text-2xl" : "text-base")}>ElephantSight</span>
      )}
    </span>
  );
}
