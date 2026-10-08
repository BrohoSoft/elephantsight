import { useQueryClient } from "@tanstack/react-query";
import QRCode from "qrcode";
import { ShieldCheck, ShieldOff } from "lucide-react";
import { useEffect, useState } from "react";
import { errorMessage, request } from "../api/client";
import { keys, useApiMutation, useMe } from "../api/hooks";
import type { Session } from "../api/types";
import { useAuth } from "../auth/AuthContext";
import { ThemeSwitcher } from "../components/ThemeSwitcher";
import { Alert, Badge, Button, CopyButton, Field, Input, Modal, Mono, PageHeader, PageLoader, Panel } from "../components/ui";

export function AccountPage() {
  const me = useMe();
  if (me.isPending) return <PageLoader />;

  return (
    <>
      <PageHeader title="Il tuo account" description={me.data!.email} />
      <div className="space-y-6">
        <ProfilePanel fullName={me.data!.fullName} />
        <Panel title="Aspetto" description="Vale per questo browser. Con «Sistema» segue l'impostazione chiara o scura del computer.">
          <div className="p-4">
            <ThemeSwitcher />
          </div>
        </Panel>
        <PasswordPanel />
        <TwoFactorPanel enabled={me.data!.twoFactorEnabled} />
      </div>
    </>
  );
}

function ProfilePanel({ fullName }: { fullName: string }) {
  const [name, setName] = useState(fullName);
  const save = useApiMutation(() => ({ path: "/me", method: "PATCH", body: { fullName: name } }), [keys.me]);

  return (
    <Panel
      title="Profilo"
      footer={<Button variant="primary" loading={save.isPending} disabled={!name.trim() || name === fullName} onClick={() => save.mutate()}>Salva</Button>}
    >
      <div className="max-w-md space-y-3 p-4">
        <Field label="Nome e cognome">
          <Input value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        {save.error && <Alert tone="bad">{errorMessage(save.error)}</Alert>}
      </div>
    </Panel>
  );
}

function PasswordPanel() {
  const { signIn } = useAuth();
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [done, setDone] = useState(false);
  const change = useApiMutation<void, Session>(() => ({ path: "/me/password", body: { currentPassword: current, newPassword: next } }), []);

  return (
    <Panel
      title="Password"
      description="Cambiandola, gli altri dispositivi escono; questo resta collegato."
      footer={
        <Button
          variant="primary"
          loading={change.isPending}
          disabled={!current || next.length < 10}
          onClick={() =>
            change.mutate(undefined, {
              onSuccess: (session) => {
                signIn(session);
                setCurrent("");
                setNext("");
                setDone(true);
              },
            })
          }
        >
          Cambia password
        </Button>
      }
    >
      <div className="grid max-w-xl gap-4 p-4 sm:grid-cols-2">
        <Field label="Password attuale">
          <Input type="password" autoComplete="current-password" value={current} onChange={(e) => { setCurrent(e.target.value); setDone(false); }} />
        </Field>
        <Field label="Nuova password" hint="Almeno 10 caratteri.">
          <Input type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} />
        </Field>
        <div className="sm:col-span-2">
          {change.error && <Alert tone="bad">{errorMessage(change.error)}</Alert>}
          {done && <Alert tone="ok">Password aggiornata.</Alert>}
        </div>
      </div>
    </Panel>
  );
}

function TwoFactorPanel({ enabled }: { enabled: boolean }) {
  const [setup, setSetup] = useState(false);
  const [disable, setDisable] = useState(false);

  return (
    <Panel
      title="Verifica in due passaggi"
      description="Oltre alla password, un codice dall'app di autenticazione del telefono. Consigliata: questo account custodisce chiavi degli store."
      actions={enabled ? <Badge tone="ok">Attiva</Badge> : <Badge>Non attiva</Badge>}
    >
      <div className="flex items-center justify-between gap-4 p-4">
        <p className="flex items-center gap-2 text-[13px] text-muted">
          {enabled ? <ShieldCheck className="size-4 text-ok" /> : <ShieldOff className="size-4 text-faint" />}
          {enabled ? "Al prossimo accesso ti chiederemo anche il codice." : "Funziona con Google Authenticator, 1Password, Authy e simili."}
        </p>
        {enabled ? (
          <Button variant="danger" onClick={() => setDisable(true)}>Disattiva</Button>
        ) : (
          <Button variant="primary" onClick={() => setSetup(true)}>Attiva</Button>
        )}
      </div>
      {setup && <SetupTwoFactorModal onClose={() => setSetup(false)} />}
      {disable && <DisableTwoFactorModal onClose={() => setDisable(false)} />}
    </Panel>
  );
}

/** Tre passi: password, QR code e primo codice, codici di recupero da salvare. */
function SetupTwoFactorModal({ onClose }: { onClose: () => void }) {
  const queryClient = useQueryClient();
  const [password, setPassword] = useState("");
  const [secret, setSecret] = useState<{ secret: string; otpAuthUri: string } | null>(null);
  const [qr, setQr] = useState<string | null>(null);
  const [code, setCode] = useState("");
  const [recovery, setRecovery] = useState<string[] | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (secret) QRCode.toDataURL(secret.otpAuthUri, { margin: 1, width: 200, color: { dark: "#121212", light: "#ffffff" } }).then(setQr);
  }, [secret]);

  const run = async (action: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    try {
      await action();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const finish = () => {
    queryClient.invalidateQueries({ queryKey: keys.me });
    onClose();
  };

  return (
    <Modal open onOpenChange={(o) => !o && (recovery ? finish() : onClose())} title="Attiva la verifica in due passaggi">
      {recovery ? (
        <div className="space-y-4">
          <Alert tone="ok" title="Verifica in due passaggi attiva" />
          <p className="text-[13px] text-muted">
            Salva questi codici di recupero in un posto sicuro. Ognuno vale una volta, e servono se perdi il telefono: <span className="text-fg">non li vedrai più</span>.
          </p>
          <div className="grid grid-cols-2 gap-2 rounded-md border border-line bg-bg p-3 font-mono text-[13px] text-fg">
            {recovery.map((c) => <span key={c}>{c}</span>)}
          </div>
          <div className="flex justify-between">
            <span className="inline-flex items-center gap-1 text-xs text-muted">Copia tutti <CopyButton value={recovery.join("\n")} /></span>
            <Button variant="primary" onClick={finish}>Li ho salvati</Button>
          </div>
        </div>
      ) : secret ? (
        <form
          className="space-y-4"
          onSubmit={(e) => {
            e.preventDefault();
            run(async () => setRecovery((await request<{ codes: string[] }>("/me/2fa/enable", { method: "POST", body: { code } })).codes));
          }}
        >
          <p className="text-[13px] text-muted">Inquadra il codice con l'app di autenticazione, poi scrivi il codice a 6 cifre che ti mostra.</p>
          <div className="flex justify-center">
            {qr ? <img src={qr} alt="QR code per l'app di autenticazione" className="size-[200px] rounded-md" /> : <div className="size-[200px]" />}
          </div>
          <p className="flex items-center justify-center gap-1 text-xs text-faint">
            Oppure inserisci a mano: <Mono>{secret.secret}</Mono> <CopyButton value={secret.secret} />
          </p>
          <Field label="Codice">
            <Input autoFocus inputMode="numeric" autoComplete="one-time-code" value={code} onChange={(e) => setCode(e.target.value)} className="h-10 text-center font-mono text-lg tracking-[0.3em]" placeholder="000000" />
          </Field>
          {error ? <Alert tone="bad">{errorMessage(error)}</Alert> : null}
          <div className="flex justify-end">
            <Button type="submit" variant="primary" loading={busy} disabled={code.length < 6}>Attiva</Button>
          </div>
        </form>
      ) : (
        <form
          className="space-y-4"
          onSubmit={(e) => {
            e.preventDefault();
            run(async () => setSecret(await request("/me/2fa/setup", { method: "POST", body: { password } })));
          }}
        >
          <Field label="Conferma la password">
            <Input type="password" autoFocus autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
          </Field>
          {error ? <Alert tone="bad">{errorMessage(error)}</Alert> : null}
          <div className="flex justify-end">
            <Button type="submit" variant="primary" loading={busy} disabled={!password}>Continua</Button>
          </div>
        </form>
      )}
    </Modal>
  );
}

function DisableTwoFactorModal({ onClose }: { onClose: () => void }) {
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const disable = useApiMutation(() => ({ path: "/me/2fa/disable", body: { password, code } }), [keys.me]);

  return (
    <Modal
      open
      onOpenChange={(o) => !o && onClose()}
      title="Disattiva la verifica in due passaggi"
      description="Servono la password e un codice dell'app, o un codice di recupero."
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>Annulla</Button>
          <Button variant="danger" loading={disable.isPending} disabled={!password || !code} onClick={() => disable.mutate(undefined, { onSuccess: onClose })}>
            Disattiva
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <Field label="Password">
          <Input type="password" autoFocus value={password} onChange={(e) => setPassword(e.target.value)} />
        </Field>
        <Field label="Codice">
          <Input className="font-mono" value={code} onChange={(e) => setCode(e.target.value)} placeholder="000000" />
        </Field>
        {disable.error && <Alert tone="bad">{errorMessage(disable.error)}</Alert>}
      </div>
    </Modal>
  );
}
