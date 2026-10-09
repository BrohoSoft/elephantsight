import { CloudDownload, FileKey2, KeyRound, Plus, RefreshCw, ShieldCheck, Trash2, Upload } from "lucide-react";
import { useRef, useState, type ReactNode } from "react";
import { errorMessage } from "../api/client";
import { canAdmin, keys, useApiMutation, useCredentials } from "../api/hooks";
import type { Credential, CredentialStatus, Store } from "../api/types";
import { StoreGlyph, storeName } from "../components/StoreIcons";
import { Alert, Badge, Button, CopyButton, EmptyState, Field, Input, Modal, Mono, PageHeader, PageLoader, Panel, Segmented } from "../components/ui";
import { formatDate, formatDateTime, useOrg } from "../components/org";

const statusBadge: Record<CredentialStatus, ReactNode> = {
  Valid: <Badge tone="ok">Funzionante</Badge>,
  Limited: <Badge tone="warn">Accesso parziale</Badge>,
  Invalid: <Badge tone="bad">Non valida</Badge>,
};

export function CredentialsPage() {
  const org = useOrg();
  const credentials = useCredentials(org.id);
  const [adding, setAdding] = useState(false);
  const admin = canAdmin(org.role);

  if (credentials.isPending) return <PageLoader />;

  return (
    <>
      <PageHeader
        title="Chiavi degli store"
        description="Le chiavi con cui ElephantSight legge i dati da App Store Connect e Google Play. Sono cifrate e non escono più dal server."
        actions={admin && <Button variant="primary" icon={<Plus className="size-3.5" />} onClick={() => setAdding(true)}>Aggiungi chiave</Button>}
      />

      {credentials.data!.length === 0 ? (
        <div className="rounded-lg border border-dashed border-line-strong">
          <EmptyState
            icon={<KeyRound className="size-5" />}
            title="Nessuna chiave"
            action={admin && <Button variant="primary" onClick={() => setAdding(true)}>Aggiungi la prima chiave</Button>}
          >
            Una chiave vale per tutte le app del tuo account sviluppatore: basta caricarla una volta per store.
          </EmptyState>
        </div>
      ) : (
        <Panel>
          <ul className="divide-y divide-line">
            {credentials.data!.map((c) => <CredentialRow key={c.id} orgId={org.id} credential={c} admin={admin} />)}
          </ul>
        </Panel>
      )}

      <AddCredentialModal orgId={org.id} open={adding} onOpenChange={setAdding} />
    </>
  );
}

function CredentialRow({ orgId, credential: c, admin }: { orgId: string; credential: Credential; admin: boolean }) {
  const [confirm, setConfirm] = useState(false);
  const verify = useApiMutation<void, Credential>(() => ({ path: `/orgs/${orgId}/credentials/${c.id}/verify` }), [keys.credentials(orgId)]);
  const remove = useApiMutation(() => ({ path: `/orgs/${orgId}/credentials/${c.id}`, method: "DELETE" }), [keys.credentials(orgId)]);
  const sync = useApiMutation(() => ({ path: `/orgs/${orgId}/credentials/${c.id}/sync` }), [keys.credentials(orgId)]);

  return (
    <li className="px-4 py-3.5">
      <div className="flex flex-wrap items-start gap-x-4 gap-y-2">
        <StoreGlyph store={c.store} className={`mt-0.5 ${c.store === "AppStore" ? "text-ios" : "text-android"}`} />
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-[0.8125rem] font-medium text-fg">{c.label}</span>
            {statusBadge[c.status]}
          </div>
          <p className="mt-1 text-xs text-muted">
            {c.store === "AppStore" ? (
              <>Key ID <Mono>{c.keyId}</Mono>{c.vendorNumber ? <> · Vendor <Mono>{c.vendorNumber}</Mono></> : <> · <span className="text-warn">senza Vendor Number</span></>}</>
            ) : (
              <>{c.clientEmail}{c.reportsBucket ? <> · <Mono>{c.reportsBucket}</Mono></> : null}</>
            )}
          </p>
          {c.statusMessage && c.status !== "Valid" && <p className="mt-1.5 text-xs text-warn">{c.statusMessage}</p>}
          <SyncStatus credential={c} />
        </div>
        {admin && (
          <div className="flex shrink-0 gap-1.5">
            <Button size="sm" variant="ghost" icon={<CloudDownload className="size-3" />} loading={sync.isPending} disabled={c.syncRequested} onClick={() => sync.mutate()}>
              {c.syncRequested ? "In coda" : "Sincronizza ora"}
            </Button>
            <Button size="sm" variant="ghost" icon={<RefreshCw className="size-3" />} loading={verify.isPending} onClick={() => verify.mutate()}>
              Verifica
            </Button>
            {confirm ? (
              <>
                <Button size="sm" variant="danger" loading={remove.isPending} onClick={() => remove.mutate()}>Elimina</Button>
                <Button size="sm" variant="ghost" onClick={() => setConfirm(false)}>No</Button>
              </>
            ) : (
              <Button size="sm" variant="ghost" icon={<Trash2 className="size-3" />} onClick={() => setConfirm(true)} aria-label="Elimina" />
            )}
          </div>
        )}
      </div>
      {(verify.error || remove.error || sync.error) && (
        <div className="mt-3">
          <Alert tone="bad">{errorMessage(verify.error ?? remove.error ?? sync.error)}</Alert>
        </div>
      )}
    </li>
  );
}

/**
 * A che punto è la sincronizzazione: quanti giorni di storico sono arrivati,
 * fino a quando, e l'eventuale errore dell'ultimo giro.
 */
function SyncStatus({ credential: c }: { credential: Credential }) {
  return (
    <div className="mt-1.5 space-y-1">
      <p className="text-[0.6875rem] text-faint">
        {c.daysImported > 0
          ? <>Storico: {c.daysImported} {c.store === "AppStore" ? "giorni controllati" : "report mensili"}{c.latestReportDate && <>, {c.store === "AppStore" ? "dati fino al" : "ultimo mese"} {formatDate(c.latestReportDate)}</>}</>
          : "Nessun report ancora scaricato"}
        {c.lastSyncCompletedAtUtc && <> · ultimo giro {formatDateTime(c.lastSyncCompletedAtUtc)}</>}
        {c.syncRequested && <> · sincronizzazione in coda</>}
      </p>
      {c.lastSyncError && <p className="text-xs text-warn">{c.lastSyncError}</p>}
    </div>
  );
}

/** Legge un file scelto dall'utente come testo: .p8 e JSON sono pochi KB. */
function FilePicker({ accept, fileName, onLoad, label }: { accept: string; fileName: string | null; onLoad: (name: string, text: string) => void; label: string }) {
  const input = useRef<HTMLInputElement>(null);
  return (
    <button
      type="button"
      onClick={() => input.current?.click()}
      className="flex w-full items-center gap-3 rounded-md border border-dashed border-line-strong bg-bg px-3 py-3 text-left hover:border-brand/50"
    >
      {fileName ? <FileKey2 className="size-5 text-brand-fg" /> : <Upload className="size-5 text-faint" />}
      <span className="min-w-0 flex-1">
        <span className="block truncate text-[0.8125rem] text-fg">{fileName ?? label}</span>
        <span className="block text-xs text-faint">{fileName ? "Clicca per sceglierne un altro" : `File ${accept}`}</span>
      </span>
      <input
        ref={input}
        type="file"
        accept={accept}
        className="hidden"
        onChange={async (e) => {
          const file = e.target.files?.[0];
          if (file) onLoad(file.name, await file.text());
          e.target.value = "";
        }}
      />
    </button>
  );
}

function Steps({ children }: { children: ReactNode }) {
  return <ol className="list-decimal space-y-1.5 rounded-md border border-line bg-panel-2/60 py-3 pr-3 pl-8 text-xs text-muted marker:text-faint">{children}</ol>;
}

function AddCredentialModal({ orgId, open, onOpenChange }: { orgId: string; open: boolean; onOpenChange: (v: boolean) => void }) {
  const [store, setStore] = useState<Store>("AppStore");
  const [label, setLabel] = useState("");
  const [file, setFile] = useState<{ name: string; text: string } | null>(null);
  const [apple, setApple] = useState({ keyId: "", issuerId: "", vendorNumber: "" });
  const [bucket, setBucket] = useState("");
  const [result, setResult] = useState<Credential | null>(null);

  // L'email del service account si legge dal JSON già nel browser: è quella
  // da invitare in Play Console, e conviene mostrarla prima del caricamento.
  const clientEmail = (() => {
    if (store !== "GooglePlay" || !file) return null;
    try {
      return (JSON.parse(file.text).client_email as string) ?? null;
    } catch {
      return null;
    }
  })();

  // Il Key ID è nel nome del file che Apple fa scaricare: AuthKey_XXXXXXXXXX.p8.
  const onAppleFile = (name: string, text: string) => {
    setFile({ name, text });
    const match = /AuthKey_([A-Z0-9]{10})\.p8$/.exec(name);
    if (match && !apple.keyId) setApple((a) => ({ ...a, keyId: match[1] }));
  };

  const create = useApiMutation<void, Credential>(
    () =>
      store === "AppStore"
        ? { path: `/orgs/${orgId}/credentials/app-store`, body: { label, ...apple, vendorNumber: apple.vendorNumber || null, privateKey: file?.text } }
        : { path: `/orgs/${orgId}/credentials/google-play`, body: { label, serviceAccountJson: file?.text, reportsBucket: bucket || null } },
    [keys.credentials(orgId)],
  );

  const reset = () => {
    setLabel("");
    setFile(null);
    setApple({ keyId: "", issuerId: "", vendorNumber: "" });
    setBucket("");
    setResult(null);
    create.reset();
  };

  const close = () => {
    onOpenChange(false);
    setTimeout(reset, 200);
  };

  const ready = label.trim() && file && (store === "GooglePlay" || (apple.keyId && apple.issuerId));

  if (result) {
    return (
      <Modal open={open} onOpenChange={(o) => !o && close()} title="Chiave salvata" footer={<Button variant="primary" onClick={close}>Fatto</Button>}>
        {result.status === "Valid" ? (
          <Alert tone="ok" title="Tutto a posto">
            {storeName(result.store)} ha accettato la chiave. È cifrata sul server e pronta per essere collegata ai progetti.
          </Alert>
        ) : (
          <Alert tone="warn" title="Salvata, ma con accesso parziale">{result.statusMessage}</Alert>
        )}
        <p className="mt-4 flex items-center gap-2 text-xs text-muted">
          <ShieldCheck className="size-4 text-ok" /> Il file non viene più mostrato né scaricato: per cambiarlo carica una chiave nuova.
        </p>
      </Modal>
    );
  }

  return (
    <Modal
      open={open}
      onOpenChange={(o) => (o ? onOpenChange(true) : close())}
      title="Aggiungi una chiave"
      description="La verifichiamo subito con lo store, poi la cifriamo sul server."
      wide
      footer={
        <>
          <Button variant="ghost" onClick={close}>Annulla</Button>
          <Button variant="primary" loading={create.isPending} disabled={!ready} onClick={() => create.mutate(undefined, { onSuccess: setResult })}>
            Verifica e salva
          </Button>
        </>
      }
    >
      <div className="space-y-5">
        <Segmented
          value={store}
          onChange={(s) => {
            reset();
            setStore(s);
          }}
          options={[
            { value: "AppStore", label: <><StoreGlyph store="AppStore" className="size-3.5" /> App Store Connect</> },
            { value: "GooglePlay", label: <><StoreGlyph store="GooglePlay" className="size-3.5" /> Google Play</> },
          ]}
        />

        {store === "AppStore" ? (
          <>
            <Steps>
              <li>In App Store Connect apri <span className="text-fg">Utenti e accesso → Integrazioni → App Store Connect API</span>.</li>
              <li>In <span className="text-fg">Chiavi del team</span> genera una chiave con accesso <span className="text-fg">Sales</span>: i download Apple stanno nel report Sales and Trends. Le chiavi individuali non lo leggono.</li>
              <li>Scarica il file <Mono>AuthKey_….p8</Mono>: Apple lo fa scaricare una volta sola.</li>
              <li>Copia l'<span className="text-fg">Issuer ID</span> in cima alla pagina e il Key ID accanto alla chiave.</li>
            </Steps>
            <FilePicker accept=".p8" label="Scegli il file .p8" fileName={file?.name ?? null} onLoad={onAppleFile} />
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Key ID">
                <Input className="font-mono" value={apple.keyId} onChange={(e) => setApple({ ...apple, keyId: e.target.value.trim() })} placeholder="ABCDE12345" />
              </Field>
              <Field label="Issuer ID">
                <Input className="font-mono" value={apple.issuerId} onChange={(e) => setApple({ ...apple, issuerId: e.target.value.trim() })} placeholder="69a6de7e-…" />
              </Field>
            </div>
            <Field label="Vendor Number" hint="In Pagamenti e resoconti finanziari, sotto il nome dell'account. Senza, i download non si possono scaricare.">
              <Input className="font-mono" value={apple.vendorNumber} onChange={(e) => setApple({ ...apple, vendorNumber: e.target.value.trim() })} placeholder="85012345" />
            </Field>
          </>
        ) : (
          <>
            <Steps>
              <li>In Google Cloud crea un <span className="text-fg">service account</span> e scarica una chiave in formato JSON. Non servono ruoli né API da abilitare.</li>
              <li>In Play Console, <span className="text-fg">Utenti e autorizzazioni</span>, invita l'email del service account con il permesso di vedere le informazioni sulle app e scaricare i report in blocco.</li>
              <li>In <span className="text-fg">Scarica report</span> copia l'URI di Cloud Storage (<Mono>gs://pubsite_prod_…</Mono>).</li>
            </Steps>
            <FilePicker accept=".json" label="Scegli il file JSON" fileName={file?.name ?? null} onLoad={(name, text) => setFile({ name, text })} />
            {clientEmail && (
              <div className="flex items-center justify-between gap-2 rounded-md border border-line bg-panel-2 px-3 py-2">
                <span className="min-w-0 text-xs text-muted">
                  Da invitare in Play Console: <span className="font-mono text-fg">{clientEmail}</span>
                </span>
                <CopyButton value={clientEmail} />
              </div>
            )}
            <Field label="Bucket dei report" hint="Serve sia per i dati sia per riconoscere le app: senza, la chiave resta ad accesso parziale.">
              <Input className="font-mono" value={bucket} onChange={(e) => setBucket(e.target.value.trim())} placeholder="gs://pubsite_prod_rev_0123456789" />
            </Field>
          </>
        )}

        <Field label="Nome della chiave" hint="Per riconoscerla, per esempio il nome del team o dell'account sviluppatore.">
          <Input value={label} onChange={(e) => setLabel(e.target.value)} placeholder={store === "AppStore" ? "Team Acme" : "Play Console Acme"} />
        </Field>

        {create.error && <Alert tone="bad">{errorMessage(create.error)}</Alert>}
      </div>
    </Modal>
  );
}
