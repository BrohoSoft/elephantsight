import { useQueryClient } from "@tanstack/react-query";
import { ShieldCheck } from "lucide-react";
import { useState, type FormEvent } from "react";
import { useNavigate } from "react-router";
import { errorMessage, request } from "../../api/client";
import { keys } from "../../api/hooks";
import type { Session } from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { AuthLayout } from "../../components/AuthLayout";
import { Alert, Button, Field, Input } from "../../components/ui";

/**
 * L'installer: compare finché l'istanza non ha utenti. Chi lo completa diventa
 * l'amministratore; da lì in poi si entra solo per invito.
 */
export function SetupPage() {
  const { signIn } = useAuth();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [form, setForm] = useState({ fullName: "", organizationName: "", email: "", password: "" });
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const set = (key: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [key]: e.target.value });

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      signIn(await request<Session>("/setup", { method: "POST", body: form, anonymous: true }));
      await queryClient.invalidateQueries({ queryKey: keys.instance });
      navigate("/", { replace: true });
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  return (
    <AuthLayout
      title="Benvenuto in ElephantSight"
      subtitle="Crea l'account amministratore. Gli altri utenti entreranno solo su tuo invito."
    >
      <form onSubmit={submit} className="space-y-4">
        <Field label="Nome e cognome">
          <Input autoFocus required autoComplete="name" value={form.fullName} onChange={set("fullName")} />
        </Field>
        <Field label="Nome dell'organizzazione" hint="Il primo spazio di lavoro. Potrai crearne altri, per esempio uno per cliente.">
          <Input required value={form.organizationName} onChange={set("organizationName")} placeholder="Il mio studio" />
        </Field>
        <Field label="Email">
          <Input type="email" required autoComplete="email" value={form.email} onChange={set("email")} />
        </Field>
        <Field label="Password" hint="Almeno 10 caratteri.">
          <Input type="password" required minLength={10} autoComplete="new-password" value={form.password} onChange={set("password")} />
        </Field>
        {error ? <Alert tone="bad">{errorMessage(error)}</Alert> : null}
        <Button type="submit" variant="primary" className="h-9 w-full" loading={busy}>
          Crea l'amministratore
        </Button>
        <p className="flex items-start gap-2 text-xs text-faint">
          <ShieldCheck className="mt-0.5 size-3.5 shrink-0" />
          Dopo il primo accesso attiva la verifica in due passaggi dal tuo account: questo pannello custodisce le chiavi dei tuoi store.
        </p>
      </form>
    </AuthLayout>
  );
}
