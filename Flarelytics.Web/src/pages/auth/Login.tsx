import { useState, type FormEvent } from "react";
import { Link, useNavigate, useSearchParams } from "react-router";
import { ApiError, errorMessage, request, requestWithStatus } from "../../api/client";
import { useInstance } from "../../api/hooks";
import type { SecondFactorChallenge, Session } from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { AuthLayout } from "../../components/AuthLayout";
import { Alert, Button, Field, Input } from "../../components/ui";

export function LoginPage() {
  const { signIn } = useAuth();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const next = params.get("next") ?? "/";

  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [challenge, setChallenge] = useState<string | null>(null);
  const [code, setCode] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const done = (session: Session) => {
    signIn(session);
    navigate(next, { replace: true });
  };

  async function submitPassword(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      // 200 è la sessione; 202 vuol dire che serve il codice della 2FA.
      const { status, data } = await requestWithStatus<Session | SecondFactorChallenge>("/auth/login", {
        method: "POST",
        body: { email, password },
        anonymous: true,
      });
      if (status === 202) setChallenge((data as SecondFactorChallenge).challengeToken);
      else done(data as Session);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  async function submitCode(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      done(await request<Session>("/auth/login/2fa", { method: "POST", body: { challengeToken: challenge, code }, anonymous: true }));
    } catch (err) {
      // Sfida scaduta o bruciata: si torna alla password.
      if (err instanceof ApiError && err.code === "challenge_expired") {
        setChallenge(null);
        setCode("");
      }
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  const instance = useInstance();

  if (challenge) {
    return (
      <AuthLayout title="Verifica in due passaggi" subtitle="Inserisci il codice a 6 cifre dell'app di autenticazione, oppure uno dei codici di recupero.">
        <form onSubmit={submitCode} className="space-y-4">
          <Field label="Codice">
            <Input
              autoFocus
              inputMode="numeric"
              autoComplete="one-time-code"
              value={code}
              onChange={(e) => setCode(e.target.value)}
              className="h-10 text-center font-mono text-lg tracking-[0.3em]"
              placeholder="000000"
            />
          </Field>
          {error ? <Alert tone="bad">{errorMessage(error)}</Alert> : null}
          <Button type="submit" variant="primary" className="h-9 w-full" loading={busy} disabled={!code.trim()}>
            Verifica
          </Button>
          <button type="button" className="w-full text-center text-xs text-muted hover:text-fg" onClick={() => setChallenge(null)}>
            Torna indietro
          </button>
        </form>
      </AuthLayout>
    );
  }

  return (
    <AuthLayout title="Bentornato" subtitle="Accedi al tuo pannello.">
      <form onSubmit={submitPassword} className="space-y-4">
        <Field label="Email">
          <Input type="email" autoComplete="email" autoFocus required value={email} onChange={(e) => setEmail(e.target.value)} placeholder="tu@azienda.it" />
        </Field>
        <Field label="Password">
          <Input type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
        </Field>
        {/* Senza email configurata il link per reimpostarla non potrebbe arrivare. */}
        {instance.data?.emailEnabled && (
          <div className="-mt-2 text-right">
            <Link to="/forgot-password" className="text-xs text-muted hover:text-fg">
              Password dimenticata?
            </Link>
          </div>
        )}

        {error ? <Alert tone="bad">{errorMessage(error)}</Alert> : null}

        <Button type="submit" variant="primary" className="h-9 w-full" loading={busy}>
          Accedi
        </Button>
      </form>
    </AuthLayout>
  );
}
