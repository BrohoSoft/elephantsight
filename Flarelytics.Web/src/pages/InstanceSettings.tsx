import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Mail, ServerCog, Trash2, UserPlus } from "lucide-react";
import { useState, type ReactNode } from "react";
import { Navigate } from "react-router";
import { errorMessage, request } from "../api/client";
import { keys, useInstance, useMe } from "../api/hooks";
import type { InstanceAdminItem, SettingsGroup, SettingValue } from "../api/types";
import { NetworkGlyph } from "../components/SocialIcons";
import { Alert, Badge, Button, CopyButton, Field, Input, Mono, PageHeader, PageLoader, Panel } from "../components/ui";

const settingsKey = ["instance", "settings"] as const;
const adminsKey = ["instance", "admins"] as const;

const labels: Record<string, string> = {
  host: "Server", port: "Porta", username: "Utente", password: "Password", fromAddress: "Indirizzo del mittente", fromName: "Nome del mittente",
  appId: "App ID", appSecret: "App secret", clientKey: "Client key", clientSecret: "Client secret",
};

const sourceBadge = (f: SettingValue) =>
  f.source === "panel" ? <Badge tone="brand">Dal pannello</Badge> : f.source === "env" ? <Badge>Dal file .env</Badge> : <Badge tone="warn">Non impostato</Badge>;

/**
 * Le impostazioni dell'installazione: SMTP e app social, senza toccare il
 * .env né riavviare. Quello che si mette qui vince sul .env; i segreti si
 * scrivono e non si rileggono più. Solo per gli amministratori dell'istanza.
 */
export function InstanceSettingsPage() {
  const me = useMe();
  const settings = useQuery({ queryKey: settingsKey, queryFn: () => request<SettingsGroup[]>("/instance/settings"), enabled: !!me.data?.isInstanceAdmin });
  const instance = useInstance();

  if (me.isPending) return <PageLoader />;
  if (!me.data?.isInstanceAdmin) return <Navigate to="/" replace />;
  if (settings.isPending || instance.isPending) return <PageLoader />;
  if (settings.error) return <Alert tone="bad">{errorMessage(settings.error)}</Alert>;

  const group = (g: SettingsGroup["group"]) => settings.data!.find((x) => x.group === g)!;
  const i = instance.data!;

  return (
    <div className="space-y-6">
      <PageHeader title="Impostazioni dell'istanza"
        description="Valgono per tutta l'installazione e per tutte le organizzazioni, e si applicano subito, senza riavviare. Quello che inserisci qui vince sul file .env; le password e i secret, una volta salvati, non si vedono più (si possono solo sostituire)." />

      <SettingsForm group={group("smtp")} title="Email (SMTP)" icon={<Mail className="size-4" />}
        description="Per mandare inviti e recupero password. Senza, gli inviti si mandano copiando il link."
        extra={<SmtpTest />} />
      <SettingsForm group={group("meta")} title="Facebook e Instagram (app Meta)" icon={<NetworkGlyph network="FacebookPage" />}
        description="Per collegare le Pagine Facebook e gli account Instagram collegati a una Pagina." redirectUri={i.metaRedirectUri} />
      <SettingsForm group={group("instagram")} title="Instagram senza Pagina" icon={<NetworkGlyph network="Instagram" />}
        description="L'Instagram App ID e il suo secret (prodotto Instagram dell'app Meta), non quelli dell'app." redirectUri={i.instagramRedirectUri} />
      <SettingsForm group={group("tiktok")} title="TikTok" icon={<NetworkGlyph network="TikTok" />}
        description="Dall'app su developers.tiktok.com (Login Kit e Content Posting API)." redirectUri={i.tikTokRedirectUri} />
      <SettingsForm group={group("threads")} title="Threads" icon={<NetworkGlyph network="Threads" />}
        description="Il Threads App ID e il suo secret, dalle impostazioni del caso d'uso Threads dell'app Meta." redirectUri={i.threadsRedirectUri} />

      <Admins />
    </div>
  );
}

function SettingsForm({ group, title, icon, description, redirectUri, extra }: {
  group: SettingsGroup;
  title: string;
  icon: ReactNode;
  description: string;
  redirectUri?: string;
  extra?: ReactNode;
}) {
  const queryClient = useQueryClient();
  // Solo i campi toccati partono: un segreto non scritto resta com'è.
  const [changes, setChanges] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<"save" | "clear" | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  const fromPanel = group.fields.some((f) => f.source === "panel");

  async function run(kind: "save" | "clear") {
    setBusy(kind);
    setError(null);
    setSaved(false);
    try {
      const result = await request<SettingsGroup[]>(`/instance/settings/${group.group}`, kind === "save" ? { method: "PUT", body: changes } : { method: "DELETE" });
      queryClient.setQueryData(settingsKey, result);
      await queryClient.invalidateQueries({ queryKey: keys.instance });
      setChanges({});
      setSaved(kind === "save");
    } catch (e) {
      setError(e);
    } finally {
      setBusy(null);
    }
  }

  return (
    <Panel title={<span className="flex items-center gap-2">{icon} {title}</span>} description={description}
      footer={
        <>
          {fromPanel && <Button variant="ghost" loading={busy === "clear"} onClick={() => run("clear")} title="Toglie i valori messi qui: torna a valere il file .env">Torna al .env</Button>}
          <span className="flex-1" />
          {extra}
          <Button variant="primary" loading={busy === "save"} disabled={Object.keys(changes).length === 0} onClick={() => run("save")}>Salva</Button>
        </>
      }>
      <div className="grid gap-4 p-4 sm:grid-cols-2">
        {group.fields.map((f) => (
          <Field key={f.name} label={labels[f.name] ?? f.name} hint={sourceBadge(f)}>
            <Input
              type={f.secret ? "password" : f.name === "port" ? "number" : "text"}
              autoComplete="off"
              className={f.secret ? "font-mono" : undefined}
              value={changes[f.name] ?? (f.secret ? "" : f.value ?? "")}
              placeholder={f.secret ? (f.set ? "•••••••• (scrivi per sostituirlo)" : "Non impostato") : f.name === "port" ? "587" : undefined}
              onChange={(e) => setChanges((c) => ({ ...c, [f.name]: e.target.value }))}
            />
          </Field>
        ))}
        {redirectUri && (
          <div className="sm:col-span-2">
            <p className="text-xs text-muted">Indirizzo di ritorno da registrare nell'app:</p>
            <span className="mt-1 flex items-center gap-1"><Mono>{redirectUri}</Mono><CopyButton value={redirectUri} /></span>
          </div>
        )}
        {error ? <div className="sm:col-span-2"><Alert tone="bad">{errorMessage(error)}</Alert></div> : null}
        {saved && <div className="sm:col-span-2"><Alert tone="ok">Salvato: vale da subito.</Alert></div>}
      </div>
    </Panel>
  );
}

/** Un'email di prova all'amministratore, con l'SMTP salvato: l'errore del server si vede così com'è. */
function SmtpTest() {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<{ ok: boolean; text: string } | null>(null);

  async function test() {
    setBusy(true);
    setResult(null);
    try {
      const r = await request<{ sentTo: string }>("/instance/settings/smtp/test", { method: "POST" });
      setResult({ ok: true, text: `Mandata a ${r.sentTo}` });
    } catch (e) {
      setResult({ ok: false, text: errorMessage(e) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <span className="flex items-center gap-2">
      {result && <span className={result.ok ? "text-xs text-ok" : "max-w-xs truncate text-xs text-bad"} title={result.text}>{result.text}</span>}
      <Button loading={busy} onClick={test}>Manda una prova</Button>
    </span>
  );
}

/** Chi amministra l'istanza: si nomina per email (deve avere già un account), e ne resta sempre almeno uno. */
function Admins() {
  const queryClient = useQueryClient();
  const me = useMe();
  const admins = useQuery({ queryKey: adminsKey, queryFn: () => request<InstanceAdminItem[]>("/instance/admins") });
  const [email, setEmail] = useState("");
  const [error, setError] = useState<unknown>(null);

  async function act(action: () => Promise<unknown>) {
    setError(null);
    try {
      await action();
      await Promise.all([queryClient.invalidateQueries({ queryKey: adminsKey }), queryClient.invalidateQueries({ queryKey: keys.me })]);
    } catch (e) {
      setError(e);
    }
  }

  return (
    <Panel title={<span className="flex items-center gap-2"><ServerCog className="size-4" /> Amministratori dell'istanza</span>}
      description="Possono cambiare queste impostazioni. Sono separati dai ruoli delle organizzazioni: un owner di un'organizzazione non amministra l'istanza.">
      <ul className="divide-y divide-line">
        {admins.data?.map((a) => (
          <li key={a.userId} className="flex items-center gap-3 px-4 py-2.5">
            <span className="min-w-0 flex-1 text-[0.8125rem] text-fg">{a.fullName} <span className="text-xs text-muted">{a.email}</span>
              {a.userId === me.data?.id && <span className="text-xs text-faint"> (tu)</span>}</span>
            <Button size="sm" variant="ghost" icon={<Trash2 className="size-3" />} aria-label={`Togli ${a.email}`}
              disabled={(admins.data?.length ?? 0) <= 1}
              onClick={() => act(() => request(`/instance/admins/${a.userId}`, { method: "DELETE" }))} />
          </li>
        ))}
      </ul>
      <form className="flex gap-2 border-t border-line p-4" onSubmit={(e) => { e.preventDefault(); act(() => request("/instance/admins", { method: "POST", body: { email } }).then(() => setEmail(""))); }}>
        <Input type="email" value={email} onChange={(e) => setEmail(e.target.value)} placeholder="email di un utente che ha già un account" />
        <Button type="submit" icon={<UserPlus className="size-3.5" />} disabled={!email.trim()}>Aggiungi</Button>
      </form>
      {error ? <div className="px-4 pb-4"><Alert tone="bad">{errorMessage(error)}</Alert></div> : null}
    </Panel>
  );
}
