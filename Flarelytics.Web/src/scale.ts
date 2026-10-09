import { useCallback, useSyncExternalStore } from "react";

/** La dimensione del pannello: compatta, normale (predefinita) o grande. Vedi styles.css. */
export type Scale = "compact" | "normal" | "large";

const storageKey = "flarelytics.scale";
const listeners = new Set<() => void>();

function read(): Scale {
  try {
    const value = localStorage.getItem(storageKey);
    return value === "compact" || value === "large" ? value : "normal";
  } catch {
    return "normal";
  }
}

function apply(scale: Scale) {
  if (scale === "normal") delete document.documentElement.dataset.scale;
  else document.documentElement.dataset.scale = scale;
}

/** Una preferenza del singolo browser, come il tema: public/theme.js la applica prima del primo disegno. */
export function useScale() {
  const scale = useSyncExternalStore(
    (notify) => {
      listeners.add(notify);
      return () => listeners.delete(notify);
    },
    read,
  );

  const setScale = useCallback((next: Scale) => {
    try {
      if (next === "normal") localStorage.removeItem(storageKey);
      else localStorage.setItem(storageKey, next);
    } catch {
      /* si applica lo stesso, solo per questa sessione */
    }
    apply(next);
    listeners.forEach((l) => l());
  }, []);

  return { scale, setScale };
}
