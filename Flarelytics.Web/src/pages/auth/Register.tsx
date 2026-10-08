import { useQuery } from "@tanstack/react-query";
import { MailCheck } from "lucide-react";
import { useState, type FormEvent } from "react";
import { Link, useNavigate, useSearchParams } from "react-router";
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

  const [form, setForm] = useState({ fullName: "", organizationName: "", email: "", password: "" });
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [sentTo, setSentTo] = useState<string | null>(null);

  const email = preview.data?.email ?? form.email;
  const set = (key: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [key]: e.target.value });

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const result = await request<{ emailConfirmationRequired: boolean }>("/auth/register", {
        method: "POST",
        anonymous: true,
        body: {
          email,
          password: form.password,
          fullName: form.fullName,
          organizationName: invite ? null : form.organizationName,
          invitationToken: invite,
        },
      });

      if (result.emailConfirmationRequired) {
        setSentTo(email);
      } else {
        // Con l'invito l'email è già confermata: si entra subito.
        signIn(await request<Session>("/auth/login", { method: "POST", body: { email, password: form.password }, anonymous: true }));
        navigate("/", { replace: true });
      }
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  if (sentTo) {
    return (
      <AuthLayout title="Controlla la posta">
        <Alert tone="ok" title="Ti abbiamo mandato un link">
          Apri l'email che abbiamo inviato a <span className="text-fg">{sentTo}</span> per attivare l'account. Il link vale 24 ore.
        </Alert>
        <div className="mt-6 flex justify-center text-muted">
          <MailCheck className="size-10" strokeWidth={1.2} />
        </div>
      </AuthLayout>
    );
  }

  if (invite && preview.isPending) return <PageLoader />;

  return (
    <AuthLayout
      title={preview.data ? `Entra in ${preview.data.organizationName}` : "Crea il tuo account"}
      subtitle={
        preview.data
          ? `${preview.data.invitedBy} ti ha invitato. Crea il tuo account per accettare.`
          : "Collega App Store e Google Play e guarda tutto in un posto solo."
      }
      footer={
        <>
          Hai già un account?{" "}
          <Link to={invite ? `/accept-invite?token=${encodeURIComponent(invite)}` : "/login"} className="text-fg underline-offset-4 hover:underline">
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
          {!invite && (
            <Field label="Nome dell'organizzazione" hint="La tua azienda o il tuo studio. Potrai invitare altre persone.">
              <Input required value={form.organizationName} onChange={set("organizationName")} placeholder="Acme S.r.l." />
            </Field>
          )}
          <Field label="Email">
            <Input type="email" required autoComplete="email" value={email} onChange={set("email")} readOnly={!!preview.data} />
          </Field>
          <Field label="Password" hint="Almeno 10 caratteri.">
            <Input type="password" required minLength={10} autoComplete="new-password" value={form.password} onChange={set("password")} />
          </Field>
          {error ? <Alert tone="bad">{errorMessage(error)}</Alert> : null}
          <Button type="submit" variant="primary" className="h-9 w-full" loading={busy}>
            {invite ? "Crea account ed entra" : "Crea account"}
          </Button>
        </form>
      )}
    </AuthLayout>
  );
}
