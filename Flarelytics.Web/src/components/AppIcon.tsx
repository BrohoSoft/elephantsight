import clsx from "clsx";
import { useState } from "react";

const SIZES = { sm: "size-6 rounded-md text-[0.6875rem]", md: "size-9 rounded-[0.5625rem] text-sm", lg: "size-14 rounded-[0.875rem] text-xl" } as const;

/**
 * L'icona di un'app o di un progetto, con gli angoli arrotondati come sugli
 * store. Finché il worker non l'ha scaricata, o se l'immagine non si carica,
 * al suo posto c'è l'iniziale del nome.
 */
export function AppIcon({ src, name, size = "md", className }: { src: string | null | undefined; name: string; size?: keyof typeof SIZES; className?: string }) {
  const [broken, setBroken] = useState(false);
  const base = clsx("shrink-0 border border-line", SIZES[size], className);

  if (src && !broken) {
    return <img src={src} alt="" loading="lazy" className={clsx(base, "object-cover")} onError={() => setBroken(true)} />;
  }

  return (
    <span aria-hidden className={clsx(base, "flex items-center justify-center bg-panel-2 font-medium text-muted")}>
      {name.trim().charAt(0).toUpperCase() || "?"}
    </span>
  );
}
