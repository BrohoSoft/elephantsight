import { AtSign, Trash2 } from "lucide-react";
import { useState, type ReactNode } from "react";
import { errorMessage, request } from "../../api/client";
import { canAdmin, keys, useApiMutation, useInstance, useSocialAccounts } from "../../api/hooks";
import type { SocialAccount, SocialNetwork } from "../../api/types";
import { formatDate, useOrg } from "../../components/org";
import { NetworkGlyph } from "../../components/SocialIcons";
import { Alert, Badge, Button, CopyButton, EmptyState, Field, Input, Modal, Mono, PageHeader, PageLoader, Panel } from "../../components/ui";
import { accountLabel, networkName } from "../../social/rules";

/** Dove torna il login di Facebook: l'organizzazione da cui è partito, perché l'indirizzo di ritorno è uno solo. */
export const metaOrgKey = "watchstore.metaOrg";

export function SocialAccountsPage() {
  const org = useOrg();
  const accounts = useSocialAccounts(org.id);
  const instance = useInstance();
  const admin = canAdmin(org.role);
  const [connecting, setConnecting] = useState<"Bluesky" | "Mastodon" | null>(null);

  if (accounts.isPending || instance.isPending) return <PageLoader />;

  return (
    <>
      <PageHeader title="Account social" description="Gli account su cui WatchStore pubblica i post del calendario. Password e token sono cifrati e non escono più dal server." />

      {admin && (
        <div className="mb-6 grid gap-3 md:grid-cols-3">
          <ConnectCard network="Bluesky" title="Bluesky" action={<Button size="sm" onClick={() => setConnecting("Bluesky")}>Collega</Button>}>
            Con una password per app, che crei e revochi dalle impostazioni di Bluesky.
          </ConnectCard>
          <ConnectCard network="Mastodon" title="Mastodon" action={<Button size="sm" onClick={() => setConnecting("Mastodon")}>Collega</Button>}>
            Su qualunque istanza, con un token creato dalle preferenze dell'account.
          </ConnectCard>
          <MetaCard enabled={instance.data!.metaEnabled} redirectUri={instance.data!.metaRedirectUri} />
        </div>
      )}

      {accounts.data!.length === 0 ? (
        <div className="rounded-lg border border-dashed border-line-strong">
          <EmptyState icon={<AtSign className="size-5" />} title="Nessun account collegato">
            Collega gli account delle tue app: poi li scegli post per post dal calendario.
          </EmptyState>
        </div>
      ) : (
        <Panel>
          <ul className="divide-y divide-line">
            {accounts.data!.map((a) => <AccountRow key={a.id} account={a} admin={admin} />)}
          </ul>
        </Panel>
      )}

      {connecting === "Bluesky" && <BlueskyModal onClose={() => setConnecting(null)} />}
      {connecting === "Mastodon" && <MastodonModal onClose={() => setConnecting(null)} />}
    </>
  );
}

function ConnectCard({ network, title, action, children }: { network: SocialNetwork; title: string; action: ReactNode; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-2 rounded-lg border border-line bg-panel p-4">
      <p className="flex items-center gap-2 text-sm font-medium text-fg"><NetworkGlyph network={network} /> {title}</p>
      <p className="flex-1 text-xs text-muted">{children}</p>
      <div>{action}</div>
    </div>
  );
}

/**
 * Instagram e Facebook passano da un'app Meta di chi installa. Se non c'è, la
 * scheda spiega come crearla invece di offrire un pulsante che non funziona.
 */
function MetaCard({ enabled, redirectUri }: { enabled: boolean; redirectUri: string }) {
  const org = useOrg();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [help, setHelp] = useState(false);

  async function start() {
    setBusy(true);
    setError(null);
    try {
      const { url } = await request<{ url: string }>(`/orgs/${org.id}/social/meta/start`, { method: "POST" });
      try {
        sessionStorage.setItem(metaOrgKey, org.id);
      } catch {
        /* senza storage si torna all'ultima organizzazione aperta */
      }
      window.location.assign(url);
    } catch (e) {
      setError(e);
      setBusy(false);
    }
  }

  return (
    <div className="flex flex-col gap-2 rounded-lg border border-line bg-panel p-4">
      <p className="flex items-center gap-2 text-sm font-medium text-fg">
        <NetworkGlyph network="Instagram" /><NetworkGlyph network="FacebookPage" /> Instagram e Facebook
      </p>
      <p className="flex-1 text-xs text-muted">
        Account Instagram professionali e Pagine Facebook, con un solo accesso a Facebook.
        {!enabled && " Prima serve un'app Meta per questa installazione."}
      </p>
      <div className="flex gap-2">
        {enabled ? <Button size="sm" loading={busy} onClick={start}>Collega con Facebook</Button> : <Button size="sm" onClick={() => setHelp(true)}>Come configurarla</Button>}
        {enabled && <Button size="sm" variant="ghost" onClick={() => setHelp(true)}>Aiuto</Button>}
      </div>
      {error ? <Alert tone="bad">{errorMessage(error)}</Alert> : null}

      <Modal open={help} onOpenChange={setHelp} wide title="L'app Meta per Instagram e Facebook"
        description="Una volta per installazione. L'app resta in modalità sviluppo: per pubblicare sui tuoi account non serve la revisione di Meta.">
        <ol className="list-decimal space-y-2.5 pl-5 text-[13px] text-muted">
          <li>Su <Mono>developers.facebook.com</Mono> crea un'app di tipo <b className="text-fg">Business</b>.</li>
          <li>Aggiungi i prodotti <b className="text-fg">Facebook Login for Business</b> e <b className="text-fg">Instagram</b> (API con accesso tramite Facebook Login).</li>
          <li>
            Negli URI di reindirizzamento OAuth validi metti:
            <span className="mt-1 flex items-center gap-1"><Mono>{redirectUri}</Mono><CopyButton value={redirectUri} /></span>
          </li>
          <li>Nei ruoli dell'app aggiungi come amministratore o tester l'account Facebook che gestisce le Pagine.</li>
          <li>
            Nel file <Mono>.env</Mono> accanto a <Mono>compose.yaml</Mono> metti <Mono>META_APP_ID</Mono> e <Mono>META_APP_SECRET</Mono> (da Impostazioni → Di base), poi <Mono>docker compose up -d</Mono>.
          </li>
          <li>L'account Instagram deve essere professionale (Business o Creator) e collegato a una Pagina Facebook.</li>
        </ol>
        <div className="mt-4">
          <Alert tone="info">
            Instagram scarica le immagini dall'indirizzo pubblico dell'istanza: deve essere raggiungibile da internet (un tunnel Cloudflare va bene).
            Se lo proteggi con Cloudflare Access, lascia libero il percorso <Mono>/api/v1/social/media/</Mono>: gli indirizzi sono firmati e scadono dopo un'ora.
          </Alert>
        </div>
      </Modal>
    </div>
  );
}

function AccountRow({ account: a, admin }: { account: SocialAccount; admin: boolean }) {
  const org = useOrg();
  const [confirm, setConfirm] = useState(false);
  const remove = useApiMutation(() => ({ path: `/orgs/${org.id}/social/accounts/${a.id}`, method: "DELETE" }), [keys.socialAccounts(org.id), keys.socialPosts(org.id)]);

  return (
    <li className="flex flex-wrap items-center gap-x-4 gap-y-1 px-4 py-3">
      <NetworkGlyph network={a.network} className="text-muted" />
      <div className="min-w-0 flex-1">
        <p className="flex flex-wrap items-center gap-2 text-[13px] text-fg">
          {a.name}
          {a.handle && a.handle !== a.name && <span className="text-muted">{a.handle}</span>}
          {a.status === "Connected" ? <Badge tone="ok">Collegato</Badge> : <Badge tone="bad">Da ricollegare</Badge>}
        </p>
        <p className="mt-0.5 text-xs text-muted">
          {networkName[a.network]} · {a.limits.maxCharacters} caratteri, fino a {a.limits.maxImages} immagini · collegato il {formatDate(a.createdAtUtc)}
        </p>
        {a.statusMessage && <p className="mt-1 text-xs text-bad">{a.statusMessage}</p>}
      </div>
      {admin && (confirm ? (
        <div className="flex gap-1.5">
          <Button size="sm" variant="danger" loading={remove.isPending} onClick={() => remove.mutate()}>Scollega</Button>
          <Button size="sm" variant="ghost" onClick={() => setConfirm(false)}>No</Button>
        </div>
      ) : (
        <Button size="sm" variant="ghost" icon={<Trash2 className="size-3" />} aria-label={`Scollega ${accountLabel(a)}`} onClick={() => setConfirm(true)} />
      ))}
      {confirm && <p className="w-full text-xs text-muted">I post già usciti restano sul calendario; quelli programmati non partiranno più su questo account.</p>}
    </li>
  );
}

function BlueskyModal({ onClose }: { onClose: () => void }) {
  const org = useOrg();
  const [handle, setHandle] = useState("");
  const [appPassword, setAppPassword] = useState("");
  const [serviceUrl, setServiceUrl] = useState("");
  const connect = useApiMutation(() => ({ path: `/orgs/${org.id}/social/accounts/bluesky`, body: { handle, appPassword, serviceUrl: serviceUrl || null } }), [keys.socialAccounts(org.id)]);

  return (
    <Modal open onOpenChange={(o) => !o && onClose()} title="Collega Bluesky"
      description="Usa una password per app (Impostazioni → Privacy e sicurezza → Password per le app), non quella dell'account."
      footer={<><Button variant="ghost" onClick={onClose}>Annulla</Button><Button variant="primary" loading={connect.isPending} disabled={!handle || !appPassword} onClick={() => connect.mutate(undefined, { onSuccess: onClose })}>Collega</Button></>}>
      <form className="space-y-4" onSubmit={(e) => { e.preventDefault(); connect.mutate(undefined, { onSuccess: onClose }); }}>
        <Field label="Handle"><Input autoFocus value={handle} onChange={(e) => setHandle(e.target.value)} placeholder="tuaapp.bsky.social" /></Field>
        <Field label="Password per app"><Input type="password" autoComplete="off" className="font-mono" value={appPassword} onChange={(e) => setAppPassword(e.target.value)} placeholder="xxxx-xxxx-xxxx-xxxx" /></Field>
        <Field label="Server (facoltativo)" hint="Solo se il tuo account non sta su bsky.social."><Input value={serviceUrl} onChange={(e) => setServiceUrl(e.target.value)} placeholder="https://bsky.social" /></Field>
        {connect.error && <Alert tone="bad">{errorMessage(connect.error)}</Alert>}
      </form>
    </Modal>
  );
}

function MastodonModal({ onClose }: { onClose: () => void }) {
  const org = useOrg();
  const [instanceUrl, setInstanceUrl] = useState("");
  const [accessToken, setAccessToken] = useState("");
  const connect = useApiMutation(() => ({ path: `/orgs/${org.id}/social/accounts/mastodon`, body: { instanceUrl, accessToken } }), [keys.socialAccounts(org.id)]);

  return (
    <Modal open onOpenChange={(o) => !o && onClose()} title="Collega Mastodon"
      description="Sulla tua istanza: Preferenze → Sviluppo → Nuova applicazione, con i permessi read:accounts, write:statuses e write:media. Poi copia il token di accesso."
      footer={<><Button variant="ghost" onClick={onClose}>Annulla</Button><Button variant="primary" loading={connect.isPending} disabled={!instanceUrl || !accessToken} onClick={() => connect.mutate(undefined, { onSuccess: onClose })}>Collega</Button></>}>
      <form className="space-y-4" onSubmit={(e) => { e.preventDefault(); connect.mutate(undefined, { onSuccess: onClose }); }}>
        <Field label="Istanza"><Input autoFocus value={instanceUrl} onChange={(e) => setInstanceUrl(e.target.value)} placeholder="mastodon.social" /></Field>
        <Field label="Token di accesso"><Input type="password" autoComplete="off" className="font-mono" value={accessToken} onChange={(e) => setAccessToken(e.target.value)} /></Field>
        {connect.error && <Alert tone="bad">{errorMessage(connect.error)}</Alert>}
      </form>
    </Modal>
  );
}
