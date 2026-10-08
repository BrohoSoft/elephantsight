import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { onLostSession, refreshSession, request, setSession } from "../api/client";
import type { Session } from "../api/types";

type Status = "loading" | "anonymous" | "authenticated";

interface AuthState {
  status: Status;
  signIn: (session: Session) => void;
  signOut: () => Promise<void>;
}

const AuthContext = createContext<AuthState | null>(null);

/**
 * Lo stato della sessione. All'avvio prova il rinnovo con il cookie: se
 * riesce l'utente è dentro senza rifare il login, altrimenti va alla pagina
 * di accesso.
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<Status>("loading");
  const queryClient = useQueryClient();

  useEffect(() => {
    refreshSession().then((s) => setStatus(s ? "authenticated" : "anonymous"));

    onLostSession(() => {
      setSession(null);
      queryClient.clear();
      setStatus("anonymous");
    });
  }, [queryClient]);

  const signIn = useCallback((session: Session) => {
    setSession(session);
    setStatus("authenticated");
  }, []);

  const signOut = useCallback(async () => {
    await request("/auth/logout", { method: "POST", anonymous: true }).catch(() => {});
    setSession(null);
    queryClient.clear();
    setStatus("anonymous");
  }, [queryClient]);

  const value = useMemo(() => ({ status, signIn, signOut }), [status, signIn, signOut]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (!context) throw new Error("useAuth fuori da AuthProvider");
  return context;
}
