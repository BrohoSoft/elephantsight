import { useCallback, useEffect, useSyncExternalStore } from "react";

export type ThemePreference = "light" | "dark" | "system";

const storageKey = "flarelytics.theme";
const media = window.matchMedia("(prefers-color-scheme: dark)");
const listeners = new Set<() => void>();

/** Una preferenza del singolo browser: se lo storage non è disponibile si resta su "system". */
function read(): ThemePreference {
  try {
    const value = localStorage.getItem(storageKey);
    return value === "light" || value === "dark" ? value : "system";
  } catch {
    return "system";
  }
}

function apply(preference: ThemePreference) {
  const dark = preference === "dark" || (preference === "system" && media.matches);
  document.documentElement.dataset.theme = dark ? "dark" : "light";
}

/**
 * Il tema scelto: chiaro, scuro o quello del sistema operativo. Con "system"
 * segue il sistema anche mentre la pagina è aperta.
 */
export function useTheme() {
  const preference = useSyncExternalStore(
    (notify) => {
      listeners.add(notify);
      return () => listeners.delete(notify);
    },
    read,
  );

  useEffect(() => {
    apply(preference);
    if (preference !== "system") return;

    const follow = () => apply("system");
    media.addEventListener("change", follow);
    return () => media.removeEventListener("change", follow);
  }, [preference]);

  const setPreference = useCallback((next: ThemePreference) => {
    try {
      if (next === "system") localStorage.removeItem(storageKey);
      else localStorage.setItem(storageKey, next);
    } catch {
      /* si applica lo stesso, solo per questa sessione */
    }
    apply(next);
    listeners.forEach((l) => l());
  }, []);

  return { preference, setPreference };
}
