import clsx from "clsx";
import type { Store } from "../api/types";
import { Badge } from "./ui";

/** Icone neutre, non i loghi: un segno grafico per riconoscere a colpo d'occhio la piattaforma. */
export function StoreGlyph({ store, className }: { store: Store; className?: string }) {
  return store === "AppStore" ? (
    <svg viewBox="0 0 16 16" className={clsx("size-4", className)} fill="none" stroke="currentColor" strokeWidth="1.4" aria-hidden>
      <rect x="2" y="2" width="12" height="12" rx="3.2" />
      <path d="M6 10.6 8.6 5.4M10 10.6 7.4 5.4M5.2 9h5.6" strokeLinecap="round" />
    </svg>
  ) : (
    <svg viewBox="0 0 16 16" className={clsx("size-4", className)} fill="none" stroke="currentColor" strokeWidth="1.4" aria-hidden>
      <path d="M4 2.6v10.8c0 .4.4.6.7.4l8.6-5.4a.5.5 0 0 0 0-.8L4.7 2.2c-.3-.2-.7 0-.7.4Z" strokeLinejoin="round" />
    </svg>
  );
}

export const storeName = (store: Store) => (store === "AppStore" ? "App Store" : "Google Play");

export function StoreBadge({ store }: { store: Store }) {
  return (
    <Badge tone={store === "AppStore" ? "ios" : "android"}>
      <StoreGlyph store={store} className="size-3" />
      {storeName(store)}
    </Badge>
  );
}
