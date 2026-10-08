import { useQuery } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { Link, Navigate, useNavigate, useSearchParams } from "react-router";
import { errorMessage, request } from "../../api/client";
import type { InvitationPreview, Session } from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { AuthLayout } from "../../components/AuthLayout";
import { Alert, Button, Field, Input, PageLoader } from "../../components/ui";

export function RegisterPage() {
  const [params] = useSearchParams();
  const invite = params.get("invite");
  const navigate = useNavigate();
  const { signIn } = useAuth();

  const preview = useQuery({
    queryKey: ["invitation", invite],
    queryFn: () => request<InvitationPreview>(`/invitations/preview?token=${encodeURIComponent(invite!)}`, { anonymous: true }),
    enabled: !!invite,
    retry: false,
  });

  const [form, setForm] = useState({ fullName: "", email: "", password: "" });
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const email = preview.data?.email ?? form.email;
  const set = (key: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [key]: e.target.value });

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await request("/auth/register", {
        method: "POST",
        anonymous: true,
        body: { email, password: form.password, fullName: form.fullName, invitationToken: invite },
      });

      // Il link dell'invito prova già l'indirizzo: si entra subito.
      signIn(await request<Session>("/auth/login", { method: "POST", body: { email, password: form.password }, anonymous: true }));
      navigate("/", { replace: true });
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  // Senza invito non ci si registra: Flarelytics non ha registrazione libera.
  if (!invite) return <Navigate to="/login" replace />;

  if (preview.isPending) return <PageLoader />;

  return (
    <AuthLayout
      title={preview.data ? `Entra in ${preview.data.organizationName}` : "Invito"}
      subtitle={preview.data ? `${preview.data.invitedBy} ti ha invitato. Crea il tuo account per accettare.` : undefined}
      footer={
        <>
          Hai già un account?{" "}
          <Link to={`/accept-invite?token=${encodeURIComponent(invite)}`} className="text-fg underline-offset-4 hover:underline">
            Accedi
          </Link>
        </>
      }
    >
      {preview.error ? (
        <Alert tone="bad">{errorMessage(preview.error)}</Alert>
      ) : (
        <form onSubmit={submit} className="space-y-4">
          <Field label="Nome e cognome">
            <Input autoFocus required autoComplete="name" value={form.fullName} onChange={set("fullName")} />
          </Field>
          <Field label="Email">
            <Input type="email" required autoComplete="email" value={email} onChange={set("email")} readOnly={!!preview.data} />
          </Field>
          <Field label="Password" hint="Almeno 10 caratteri.">
            <Input type="password" required minLength={10} autoComplete="new-password" value={form.password} onChange={set("password")} />
          </Field>
          {error ? <Alert tone="bad">{errorMessage(error)}</Alert> : null}
          <Button type="submit" variant="primary" className="h-9 w-full" loading={busy}>
            Crea account ed entra
          </Button>
        </form>
      )}
    </AuthLayout>
  );
}
