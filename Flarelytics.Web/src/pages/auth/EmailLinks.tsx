import { useMutation } from "@tanstack/react-query";
import { useEffect, useRef, useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router";
import { errorMessage, request } from "../../api/client";
import { AuthLayout } from "../../components/AuthLayout";
import { Alert, Button, Field, Input, PageLoader } from "../../components/ui";

/** La pagina del link di conferma: manda il token appena si apre. */
export function ConfirmEmailPage() {
  const [params] = useSearchParams();
  const token = params.get("token") ?? "";
  const confirm = useMutation({ mutationFn: () => request("/auth/confirm-email", { method: "POST", body: { token }, anonymous: true }) });

  // Una volta sola anche in StrictMode, che in sviluppo monta i componenti due
  // volte: il secondo invio troverebbe il token già usato e mostrerebbe un errore.
  const sent = useRef(false);
  useEffect(() => {
    if (!sent.current) {
      sent.current = true;
      confirm.mutate();
    }
  }, [confirm]);

  return (
    <AuthLayout title="Conferma dell'email">
      {confirm.isPending || confirm.isIdle ? (
        <PageLoader />
      ) : confirm.isSuccess ? (
        <div className="space-y-4">
          <Alert tone="ok" title="Email confermata">Ora puoi accedere.</Alert>
          <Link to="/login">
            <Button variant="primary" className="h-9 w-full">Accedi</Button>
          </Link>
        </div>
      ) : (
        <Alert tone="bad">{errorMessage(confirm.error)}</Alert>
      )}
    </AuthLayout>
  );
}

export function ForgotPasswordPage() {
  const [email, setEmail] = useState("");
  const send = useMutation({ mutationFn: () => request("/auth/forgot-password", { method: "POST", body: { email }, anonymous: true }) });

  return (
    <AuthLayout
      title="Password dimenticata"
      subtitle="Ti mandiamo un link per sceglierne una nuova."
      footer={<Link to="/login" className="hover:text-fg">Torna all'accesso</Link>}
    >
      {send.isSuccess ? (
        <Alert tone="ok" title="Controlla la posta">Se esiste un account con questo indirizzo, ti è arrivato un link. Vale un'ora.</Alert>
      ) : (
        <form
          className="space-y-4"
          onSubmit={(e: FormEvent) => {
            e.preventDefault();
            send.mutate();
          }}
        >
          <Field label="Email">
            <Input type="email" required autoFocus value={email} onChange={(e) => setEmail(e.target.value)} />
          </Field>
          {send.error && <Alert tone="bad">{errorMessage(send.error)}</Alert>}
          <Button type="submit" variant="primary" className="h-9 w-full" loading={send.isPending}>Manda il link</Button>
        </form>
      )}
    </AuthLayout>
  );
}

export function ResetPasswordPage() {
  const [params] = useSearchParams();
  const [password, setPassword] = useState("");
  const reset = useMutation({
    mutationFn: () => request("/auth/reset-password", { method: "POST", body: { token: params.get("token"), password }, anonymous: true }),
  });

  return (
    <AuthLayout title="Nuova password" subtitle="Tutte le sessioni aperte verranno chiuse.">
      {reset.isSuccess ? (
        <div className="space-y-4">
          <Alert tone="ok" title="Password aggiornata">Accedi con la password nuova.</Alert>
          <Link to="/login"><Button variant="primary" className="h-9 w-full">Accedi</Button></Link>
        </div>
      ) : (
        <form
          className="space-y-4"
          onSubmit={(e: FormEvent) => {
            e.preventDefault();
            reset.mutate();
          }}
        >
          <Field label="Password" hint="Almeno 10 caratteri.">
            <Input type="password" required minLength={10} autoFocus autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />
          </Field>
          {reset.error && <Alert tone="bad">{errorMessage(reset.error)}</Alert>}
          <Button type="submit" variant="primary" className="h-9 w-full" loading={reset.isPending}>Salva</Button>
        </form>
      )}
    </AuthLayout>
  );
}
