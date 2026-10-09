import { KeySquare, Plus, Trash2 } from "lucide-react";
import { useState } from "react";
import { errorMessage } from "../api/client";
import { canAdmin, keys, useApiKeys, useApiMutation } from "../api/hooks";
import type { ApiKeyItem, CreatedApiKey } from "../api/types";
import { formatDate, formatDateTime, useOrg } from "../components/org";
import { Alert, Button, CopyButton, EmptyState, Field, Input, Modal, Mono, PageHeader, PageLoader, Panel } from "../components/ui";

/**
 * Le chiavi con cui altri programmi mandano post nella coda "Da programmare".
 * Una chiave non pubblica niente da sola: account e ora li sceglie chi usa il
 * pannello, quindi una chiave persa al massimo riempie la coda.
 */
export function ApiKeysPage() {
  const org = useOrg();
  const admin = canAdmin(org.role);
  const apiKeys = useApiKeys(org.id, admin);
  const [creating, setCreating] = useState(false);

  if (!admin) {
    return (
      <>
        <PageHeader title="Chiavi API" />
        <Alert tone="info">Le chiavi API le gestiscono gli amministratori dell'organizzazione.</Alert>
      </>
    );
  }
  if (apiKeys.isPending) return <PageLoader />;

  return (
    <>
      <PageHeader
        title="Chiavi API"
        description="Per mandare post a WatchStore da altri programmi: finiscono nella coda Da programmare, dove scegli account e ora."
        actions={<Button variant="primary" icon={<Plus className="size-3.5" />} onClick={() => setCreating(true)}>Nuova chiave</Button>}
      />

      {apiKeys.data!.length === 0 ? (
        <div className="mb-6 rounded-lg border border-dashed border-line-strong">
          <EmptyState icon={<KeySquare className="size-5" />} title="Nessuna chiave" action={<Button variant="primary" onClick={() => setCreating(true)}>Crea la prima chiave</Button>}>
            Una chiave per programma (il CMS del sito, uno script, un'automazione): così, se una va revocata, le altre continuano a funzionare.
          </EmptyState>
        </div>
      ) : (
        <Panel className="mb-6">
          <ul className="divide-y divide-line">{apiKeys.data!.map((k) => <KeyRow key={k.id} apiKey={k} />)}</ul>
        </Panel>
      )}

      <Usage />
      {creating && <CreateKeyModal onClose={() => setCreating(false)} />}
    </>
  );
}

function KeyRow({ apiKey: k }: { apiKey: ApiKeyItem }) {
  const org = useOrg();
  const [confirm, setConfirm] = useState(false);
  const revoke = useApiMutation(() => ({ path: `/orgs/${org.id}/api-keys/${k.id}`, method: "DELETE" }), [keys.apiKeys(org.id)]);

  return (
    <li className="flex flex-wrap items-center gap-x-4 gap-y-1 px-4 py-3">
      <KeySquare className="size-4 text-faint" />
      <div className="min-w-0 flex-1">
        <p className="text-[13px] text-fg">{k.name} <Mono>{k.prefix}…</Mono></p>
        <p className="mt-0.5 text-xs text-muted">
          creata il {formatDate(k.createdAtUtc)}{k.createdBy && <> da {k.createdBy}</>} · {k.lastUsedAtUtc ? <>usata l'ultima volta il {formatDateTime(k.lastUsedAtUtc)}</> : "mai usata"}
        </p>
      </div>
      {confirm ? (
        <div className="flex gap-1.5">
          <Button size="sm" variant="danger" loading={revoke.isPending} onClick={() => revoke.mutate()}>Revoca</Button>
          <Button size="sm" variant="ghost" onClick={() => setConfirm(false)}>No</Button>
        </div>
      ) : <Button size="sm" variant="ghost" icon={<Trash2 className="size-3" />} aria-label={`Revoca ${k.name}`} onClick={() => setConfirm(true)} />}
    </li>
  );
}

function CreateKeyModal({ onClose }: { onClose: () => void }) {
  const org = useOrg();
  const [name, setName] = useState("");
  const create = useApiMutation<void, CreatedApiKey>(() => ({ path: `/orgs/${org.id}/api-keys`, body: { name } }), [keys.apiKeys(org.id)]);
  const secret = create.data?.secret;

  return (
    <Modal open onOpenChange={(o) => !o && onClose()} title={secret ? "Copia la chiave adesso" : "Nuova chiave API"}
      description={secret ? "Non la potrai più rivedere: WatchStore ne conserva solo un'impronta. Se la perdi, revocala e creane un'altra." : "Dalle il nome del programma che la userà."}
      footer={secret
        ? <Button variant="primary" onClick={onClose}>Fatto, l'ho copiata</Button>
        : <><Button variant="ghost" onClick={onClose}>Annulla</Button><Button variant="primary" loading={create.isPending} disabled={!name.trim()} onClick={() => create.mutate()}>Crea</Button></>}>
      {secret ? (
        <div className="flex items-center gap-2 rounded-md border border-line-strong bg-field px-3 py-2">
          <code className="min-w-0 flex-1 font-mono text-xs break-all text-fg">{secret}</code>
          <CopyButton value={secret} />
        </div>
      ) : (
        <form className="space-y-3" onSubmit={(e) => { e.preventDefault(); if (name.trim()) create.mutate(); }}>
          <Field label="Nome"><Input autoFocus value={name} onChange={(e) => setName(e.target.value)} placeholder="CMS del sito" /></Field>
          {create.error && <Alert tone="bad">{errorMessage(create.error)}</Alert>}
        </form>
      )}
    </Modal>
  );
}

/** Come si usa, con l'indirizzo vero di questa installazione negli esempi. */
function Usage() {
  const base = `${window.location.origin}/api/v1/public`;
  const upload = `curl -H "Authorization: Bearer $WATCHSTORE_KEY" \\\n  -F "file=@copertina.jpg" \\\n  ${base}/media`;
  const post = `curl -H "Authorization: Bearer $WATCHSTORE_KEY" \\\n  -H "Content-Type: application/json" \\\n  -d '{
    "text": "Nuovo articolo sul blog https://esempio.it/articolo #novita",
    "suggestedAtUtc": "2026-10-20T08:00:00Z",
    "media": [{ "id": "<id dell'immagine>", "altText": "La copertina" }],
    "externalRef": "cms-123"
  }' \\\n  ${base}/posts`;

  return (
    <Panel title="Come si usa" description="Tutte le richieste con l'header Authorization: Bearer <chiave>. Al massimo 120 richieste al minuto per chiave.">
      <div className="space-y-4 p-4 text-[13px] text-muted">
        <ol className="list-decimal space-y-1 pl-5">
          <li><Mono>POST /media</Mono> con un'immagine <b className="text-fg">JPEG</b> nel campo <Mono>file</Mono> (al massimo 10 MB; per Bluesky meno di 1 MB): restituisce l'<Mono>id</Mono>.</li>
          <li><Mono>POST /posts</Mono> con testo, immagini, data proposta (facoltativa) e un tuo riferimento (facoltativo: rimandare lo stesso <Mono>externalRef</Mono> non crea doppioni).</li>
          <li><Mono>GET /posts/&#123;id&#125;</Mono> per sapere com'è andata: <Mono>Inbox</Mono>, <Mono>Scheduled</Mono>, <Mono>Published</Mono> con i link, <Mono>Failed</Mono> con il motivo.</li>
          <li><Mono>DELETE /posts/&#123;id&#125;</Mono> per ritirarlo, finché è ancora in coda.</li>
        </ol>
        <Snippet title="1. Carica un'immagine" code={upload} />
        <Snippet title="2. Manda il post" code={post} />
      </div>
    </Panel>
  );
}

function Snippet({ title, code }: { title: string; code: string }) {
  return (
    <div>
      <p className="mb-1 flex items-center justify-between text-xs font-medium text-fg">{title}<CopyButton value={code} /></p>
      <pre className="overflow-x-auto rounded-md border border-line bg-panel-2 p-3 font-mono text-xs text-fg">{code}</pre>
    </div>
  );
}
