import type { Session } from "./types";

/**
 * Un errore dell'API, letto dal Problem Details. Si decide sempre sul `code`,
 * che è stabile; `detail` è la frase da mostrare.
 */
export class ApiError extends Error {
  constructor(
    public status: number,
    public code: string,
    public detail: string,
    public fieldErrors: Record<string, string[]> = {},
  ) {
    super(detail);
  }
}

/*
 * L'access token vive solo qui, in memoria: niente localStorage, dove uno
 * script iniettato potrebbe leggerlo. A ogni ricarica della pagina si perde e
 * lo restituisce il refresh, che usa il cookie HttpOnly.
 */
let accessToken: string | null = null;
let expiresAt = 0;
let refreshing: Promise<Session | null> | null = null;
let onSessionLost: () => void = () => {};

export function setSession(session: Session | null) {
  accessToken = session?.accessToken ?? null;
  expiresAt = session ? Date.parse(session.accessTokenExpiresAtUtc) : 0;
}

export function onLostSession(handler: () => void) {
  onSessionLost = handler;
}

/**
 * Rinnova la sessione con il cookie.
 *
 * Una sola richiesta in volo per volta: il refresh token cambia a ogni uso, e
 * due rinnovi in parallelo con lo stesso cookie farebbero sembrare il secondo
 * un furto, chiudendo tutte le sessioni dell'utente.
 */
export function refreshSession(): Promise<Session | null> {
  refreshing ??= fetch("/api/v1/auth/refresh", { method: "POST", credentials: "same-origin" })
    .then(async (r) => {
      const session = r.ok ? ((await r.json()) as Session) : null;
      setSession(session);
      return session;
    })
    .catch(() => null)
    .finally(() => {
      refreshing = null;
    });

  return refreshing;
}

async function readError(response: Response): Promise<ApiError> {
  try {
    const body = await response.json();
    return new ApiError(
      response.status,
      body.code ?? "error",
      body.detail ?? body.title ?? "Si è verificato un errore.",
      body.errors ?? {},
    );
  } catch {
    return new ApiError(response.status, "error", "Si è verificato un errore.");
  }
}

interface RequestOptions {
  method?: string;
  body?: unknown;
  /** Le rotte di accesso non hanno un token da mandare e non devono tentare il rinnovo. */
  anonymous?: boolean;
  /** La risposta è un file da scaricare, non JSON. */
  blob?: boolean;
}

/** Restituisce il corpo e lo stato: il login distingue 200 (sessione) da 202 (serve il codice). */
export async function requestWithStatus<T>(path: string, options: RequestOptions = {}): Promise<{ status: number; data: T }> {
  // I file viaggiano come FormData: il browser mette da sé il Content-Type
  // con il boundary del multipart, quindi qui non va impostato.
  const isForm = options.body instanceof FormData;
  const send = () =>
    fetch(`/api/v1${path}`, {
      method: options.method ?? "GET",
      credentials: "same-origin",
      headers: {
        ...(options.body !== undefined && !isForm ? { "Content-Type": "application/json" } : {}),
        ...(!options.anonymous && accessToken ? { Authorization: `Bearer ${accessToken}` } : {}),
      },
      body: options.body === undefined ? undefined : isForm ? (options.body as FormData) : JSON.stringify(options.body),
    });

  // Si rinnova un po' prima della scadenza invece di aspettare il 401.
  if (!options.anonymous && (!accessToken || Date.now() > expiresAt - 30_000)) {
    await refreshSession();
  }

  let response = await send();

  if (response.status === 401 && !options.anonymous) {
    const renewed = await refreshSession();
    if (renewed) response = await send();
    if (response.status === 401) {
      onSessionLost();
    }
  }

  if (!response.ok) throw await readError(response);
  if (options.blob) return { status: response.status, data: (await response.blob()) as T };

  const text = await response.text();
  return { status: response.status, data: (text ? JSON.parse(text) : undefined) as T };
}

/** Scarica la risposta come file, con il nome che dà il server. */
export async function downloadFile(path: string, body: unknown, fallbackName: string) {
  const blob = await request<Blob>(path, { method: "POST", body, blob: true });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = fallbackName;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  return (await requestWithStatus<T>(path, options)).data;
}

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    const fields = Object.values(error.fieldErrors).flat();
    return fields.length > 0 ? fields.join(" ") : error.detail;
  }
  if (error instanceof Error && error.message) return error.message;
  return "Impossibile contattare il server.";
}

/** Il token valido da usare fuori da request(), per esempio con XMLHttpRequest. */
export async function currentAccessToken(): Promise<string | null> {
  if (!accessToken || Date.now() > expiresAt - 30_000) await refreshSession();
  return accessToken;
}
