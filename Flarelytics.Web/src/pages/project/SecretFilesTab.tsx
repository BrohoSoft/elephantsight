import { Download, FileKey2, Plus, ShieldCheck, Trash2, Upload } from "lucide-react";
import { useRef, useState } from "react";
import { downloadFile, errorMessage, request } from "../../api/client";
import { canAdmin, keys, useMe, useSecretFiles } from "../../api/hooks";
import type { Project, SecretFile, SecretKind, SecretPlatform } from "../../api/types";
import { formatDate, formatDateTime, useOrg } from "../../components/org";
import { Alert, Badge, Button, EmptyState, Field, Input, Modal, Mono, PageLoader, Panel, Segmented, Select, Textarea } from "../../components/ui";
import { useQueryClient } from "@tanstack/react-query";

const PLATFORMS: Record<SecretPlatform, string> = { Android: "Android", Ios: "iOS", Common: "Comuni" };
const KINDS: Record<SecretKind, { label: string; platform: SecretPlatform; accept?: string }> = {
  AndroidKeystore: { label: "Keystore (.jks)", platform: "Android", accept: ".jks,.keystore" },
  KeyProperties: { label: "key.properties", platform: "Android" },
  GoogleServicesJson: { label: "google-services.json", platform: "Android", accept: ".json" },
  IosCertificate: { label: "Certificato (.p12)", platform: "Ios", accept: ".p12" },
  ProvisioningProfile: { label: "Profilo di provisioning", platform: "Ios", accept: ".mobileprovision,.provisionprofile" },
  GoogleServiceInfoPlist: { label: "GoogleService-Info.plist", platform: "Ios", accept: ".plist" },
  Environment: { label: "Variabili / .env", platform: "Common" },
  Other: { label: "Altro", platform: "Common" },
};

/** Il nome del file per un contenuto incollato: quello che si aspetta chi lo usa. */
const PASTED_FILE_NAMES: Partial<Record<SecretKind, string>> = {
  KeyProperties: "key.properties", Environment: ".env", GoogleServicesJson: "google-services.json", GoogleServiceInfoPlist: "GoogleService-Info.plist",
};

/**
 * La cassaforte del progetto: i file che servono a firmare e configurare le
 * app. Cifrati sul server; per riscaricarli si conferma di nuovo chi si è.
 */
export function SecretFilesTab({ project }: { project: Project }) {
  const org = useOrg();
  const files = useSecretFiles(org.id, project.id);
  const [adding, setAdding] = useState(false);
  const [downloading, setDownloading] = useState<SecretFile | null>(null);
  const admin = canAdmin(org.role);

  if (files.isPending) return <PageLoader />;

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="flex items-center gap-2 text-[13px] text-muted">
          <ShieldCheck className="size-4 text-ok" /> Cifrati sul server. Per scaricarli servono la password e, se attiva, la verifica in due passaggi.
        </p>
        {admin && <Button variant="primary" icon={<Plus className="size-3.5" />} onClick={() => setAdding(true)}>Aggiungi file</Button>}
      </div>

      {files.data!.length === 0 ? (
        <div className="rounded-lg border border-dashed border-line-strong">
          <EmptyState icon={<FileKey2 className="size-5" />} title="Nessun file">
            Keystore, key.properties, certificati .p12, profili di provisioning, google-services.json: tienili qui invece che sparsi fra computer e chat.
          </EmptyState>
        </div>
      ) : (
        (Object.keys(PLATFORMS) as SecretPlatform[]).map((platform) => {
          const group = files.data!.filter((f) => f.platform === platform);
          if (group.length === 0) return null;
          return (
            <Panel key={platform} title={PLATFORMS[platform]}>
              <ul className="divide-y divide-line">
                {group.map((f) => <FileRow key={f.id} file={f} project={project} admin={admin} onDownload={() => setDownloading(f)} />)}
              </ul>
            </Panel>
          );
        })
      )}

      {adding && <AddFileModal project={project} onClose={() => setAdding(false)} />}
      {downloading && <DownloadModal project={project} file={downloading} onClose={() => setDownloading(null)} />}
    </div>
  );
}

function FileRow({ file: f, project, admin, onDownload }: { file: SecretFile; project: Project; admin: boolean; onDownload: () => void }) {
  const org = useOrg();
  const queryClient = useQueryClient();
  const [confirm, setConfirm] = useState(false);

  const remove = async () => {
    await request(`/orgs/${org.id}/projects/${project.id}/files/${f.id}`, { method: "DELETE" });
    await queryClient.invalidateQueries({ queryKey: keys.files(org.id, project.id) });
  };

  return (
    <li className="flex flex-wrap items-center gap-x-4 gap-y-1 px-4 py-3">
      <FileKey2 className="size-4 shrink-0 text-faint" />
      <div className="min-w-0 flex-1">
        <p className="flex flex-wrap items-center gap-2 text-[13px] text-fg">{f.name} <Badge>{KINDS[f.kind].label}</Badge></p>
        <p className="mt-0.5 text-xs text-muted">
          <Mono>{f.fileName}</Mono> · {(f.sizeBytes / 1024).toFixed(1)} KB · SHA-256 <span className="font-mono" title={f.sha256}>{f.sha256.slice(0, 12)}…</span> · caricato il {formatDate(f.createdAtUtc)}
          {f.lastDownloadedAtUtc && <> · ultimo download {formatDateTime(f.lastDownloadedAtUtc)}</>}
        </p>
        {f.notes && <p className="mt-1 text-xs whitespace-pre-line text-faint">{f.notes}</p>}
      </div>
      {admin && (
        <div className="flex gap-1.5">
          <Button size="sm" icon={<Download className="size-3" />} onClick={onDownload}>Scarica</Button>
          {confirm ? (
            <>
              <Button size="sm" variant="danger" onClick={remove}>Elimina</Button>
              <Button size="sm" variant="ghost" onClick={() => setConfirm(false)}>No</Button>
            </>
          ) : <Button size="sm" variant="ghost" icon={<Trash2 className="size-3" />} aria-label="Elimina" onClick={() => setConfirm(true)} />}
        </div>
      )}
    </li>
  );
}

function AddFileModal({ project, onClose }: { project: Project; onClose: () => void }) {
  const org = useOrg();
  const queryClient = useQueryClient();
  const [kind, setKind] = useState<SecretKind>("AndroidKeystore");
  const [mode, setMode] = useState<"file" | "text">("file");
  const [file, setFile] = useState<File | null>(null);
  const [text, setText] = useState("");
  const [name, setName] = useState("");
  const [notes, setNotes] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const input = useRef<HTMLInputElement>(null);

  async function save() {
    setBusy(true);
    setError(null);
    const form = new FormData();
    form.append("platform", KINDS[kind].platform);
    form.append("kind", kind);
    form.append("name", name || (mode === "file" ? file?.name ?? "" : KINDS[kind].label));
    form.append("notes", notes);
    if (mode === "file" && file) form.append("file", file);
    // Il testo incollato parte come file e non come campo: un campo di testo
    // nel multipart ha gli a capo convertiti in \r\n dal browser, un file no.
    else form.append("file", new Blob([text], { type: "text/plain" }), PASTED_FILE_NAMES[kind] ?? `${name || "segreto"}.txt`);
    try {
      await request(`/orgs/${org.id}/projects/${project.id}/files`, { method: "POST", body: form });
      await queryClient.invalidateQueries({ queryKey: keys.files(org.id, project.id) });
      onClose();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open onOpenChange={(o) => !o && onClose()} title="Aggiungi un file" wide
      footer={<><Button variant="ghost" onClick={onClose}>Annulla</Button><Button variant="primary" loading={busy} disabled={mode === "file" ? !file : !text.trim()} onClick={save}>Salva cifrato</Button></>}>
      <div className="space-y-4">
        <Field label="Tipo">
          <Select value={kind} onChange={(e) => { setKind(e.target.value as SecretKind); setFile(null); }}>
            {(Object.keys(KINDS) as SecretKind[]).map((k) => <option key={k} value={k}>{PLATFORMS[KINDS[k].platform]} · {KINDS[k].label}</option>)}
          </Select>
        </Field>
        <Segmented value={mode} onChange={setMode} options={[{ value: "file", label: "Carica un file" }, { value: "text", label: "Incolla il contenuto" }]} />
        {mode === "file" ? (
          <button type="button" onClick={() => input.current?.click()} className="flex w-full items-center gap-3 rounded-md border border-dashed border-line-strong bg-field px-3 py-3 text-left hover:border-brand/50">
            <Upload className="size-5 text-faint" />
            <span className="text-[13px] text-fg">{file ? file.name : "Scegli il file"}</span>
            <input ref={input} type="file" accept={KINDS[kind].accept} className="hidden" onChange={(e) => { setFile(e.target.files?.[0] ?? null); e.target.value = ""; }} />
          </button>
        ) : (
          <Textarea rows={6} value={text} onChange={(e) => setText(e.target.value)} placeholder={kind === "KeyProperties" ? "storePassword=…\nkeyPassword=…\nkeyAlias=upload\nstoreFile=release.jks" : ""} />
        )}
        <Field label="Nome (facoltativo)"><Input value={name} onChange={(e) => setName(e.target.value)} placeholder="Keystore di rilascio" /></Field>
        <Field label="Note (facoltative)" hint="Per esempio alias, scadenza del certificato, dove si usa. Non mettere qui le password: vanno nel file.">
          <Textarea rows={2} className="font-sans text-[13px]" value={notes} onChange={(e) => setNotes(e.target.value)} />
        </Field>
        {error ? <Alert tone="bad">{errorMessage(error)}</Alert> : null}
      </div>
    </Modal>
  );
}

function DownloadModal({ project, file, onClose }: { project: Project; file: SecretFile; onClose: () => void }) {
  const org = useOrg();
  const me = useMe();
  const queryClient = useQueryClient();
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const needsCode = me.data?.twoFactorEnabled ?? false;

  async function download() {
    setBusy(true);
    setError(null);
    try {
      await downloadFile(`/orgs/${org.id}/projects/${project.id}/files/${file.id}/download`, { password, code: code || null }, file.fileName);
      await queryClient.invalidateQueries({ queryKey: keys.files(org.id, project.id) });
      onClose();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open onOpenChange={(o) => !o && onClose()} title={`Scarica ${file.fileName}`} description="Conferma chi sei: il file esce dalla cassaforte in chiaro."
      footer={<><Button variant="ghost" onClick={onClose}>Annulla</Button><Button variant="primary" loading={busy} disabled={!password || (needsCode && !code)} onClick={download}>Scarica</Button></>}>
      <form className="space-y-4" onSubmit={(e) => { e.preventDefault(); download(); }}>
        <Field label="Password"><Input type="password" autoFocus autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} /></Field>
        {needsCode && <Field label="Codice della verifica in due passaggi"><Input inputMode="numeric" autoComplete="one-time-code" className="font-mono" value={code} onChange={(e) => setCode(e.target.value)} placeholder="000000" /></Field>}
        {!needsCode && <Alert tone="warn">Attiva la verifica in due passaggi dal tuo account: protegge anche questi file.</Alert>}
        {error ? <Alert tone="bad">{errorMessage(error)}</Alert> : null}
      </form>
    </Modal>
  );
}
