import { CloudUpload, FileArchive, RefreshCw } from "lucide-react";
import { useRef, useState } from "react";
import { currentAccessToken, errorMessage } from "../../api/client";
import { canAdmin, keys, useBuildUploads, useReleases } from "../../api/hooks";
import type { BuildUploadItem, Project, Store, StoreReleases } from "../../api/types";
import { formatDateTime, useOrg } from "../../components/org";
import { StageBadge, trackLabel } from "../../components/StageBadge";
import { StoreGlyph, storeName } from "../../components/StoreIcons";
import { Alert, Badge, Button, EmptyState, Field, Input, PageLoader, Panel, Segmented, Select, Spinner, Textarea } from "../../components/ui";
import { useQueryClient } from "@tanstack/react-query";

/** Versioni, build e caricamento di nuove build, App Store e Google Play affiancati. */
export function ReleasesTab({ project }: { project: Project }) {
  const org = useOrg();
  const releases = useReleases(org.id, project.id);
  const queryClient = useQueryClient();

  if (project.apps.length === 0) {
    return <EmptyState title="Nessuna app collegata">Collega l'app App Store o Google Play dalle Impostazioni del progetto.</EmptyState>;
  }

  return (
    <div className="space-y-6">
      {canAdmin(org.role) && <UploadPanel project={project} />}
      <UploadHistory project={project} />

      <div className="flex items-center justify-between">
        <h2 className="text-sm font-medium text-fg">Versioni e build sugli store</h2>
        <Button size="sm" variant="ghost" icon={<RefreshCw className="size-3" />} loading={releases.isFetching}
          onClick={() => queryClient.invalidateQueries({ queryKey: keys.releases(org.id, project.id) })}>
          Aggiorna
        </Button>
      </div>

      {releases.isPending ? <PageLoader /> : (
        <div className="grid gap-6 lg:grid-cols-2">
          {releases.data?.map((r) => <StoreColumn key={r.store} releases={r} />)}
        </div>
      )}
    </div>
  );
}

function StoreColumn({ releases: r }: { releases: StoreReleases }) {
  const tint = r.store === "AppStore" ? "text-ios" : "text-android";
  return (
    <div className="space-y-4">
      <div className="flex items-center gap-2">
        <StoreGlyph store={r.store} className={tint} />
        <span className="text-[13px] font-medium text-fg">{storeName(r.store)}</span>
        <span className="font-mono text-xs text-faint">{r.appId}</span>
      </div>

      {r.error && <Alert tone="warn">{r.error}</Alert>}

      <Panel title={r.store === "AppStore" ? "Versioni" : "Release per canale"}>
        {r.versions.length === 0 ? <p className="px-4 py-6 text-center text-xs text-muted">Nessuna versione.</p> : (
          <ul className="divide-y divide-line">
            {r.versions.map((v, i) => (
              <li key={`${v.track}-${v.version}-${i}`} className="flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2.5">
                <span className="font-mono text-[13px] text-fg">{v.version}</span>
                <StageBadge stage={v.stage} raw={v.rawState} />
                {v.track && <Badge>{trackLabel(v.track)}</Badge>}
                {v.rolloutPercent != null && <span className="text-xs text-muted">{v.rolloutPercent.toFixed(0)}% degli utenti</span>}
                {v.createdAtUtc && <span className="ml-auto text-xs text-faint">{formatDateTime(v.createdAtUtc)}</span>}
                {v.releaseNotes && <p className="w-full truncate text-xs text-muted" title={v.releaseNotes}>{v.releaseNotes}</p>}
              </li>
            ))}
          </ul>
        )}
      </Panel>

      <Panel title={r.store === "AppStore" ? "Build (TestFlight)" : "Bundle caricati"}>
        {r.builds.length === 0 ? <p className="px-4 py-6 text-center text-xs text-muted">Nessuna build.</p> : (
          <ul className="divide-y divide-line">
            {r.builds.slice(0, 15).map((b) => (
              <li key={b.buildNumber} className="flex flex-wrap items-center gap-3 px-4 py-2">
                <span className="font-mono text-[13px] text-fg">{b.version ? `${b.version} (${b.buildNumber})` : b.buildNumber}</span>
                {r.store === "AppStore" ? <StageBadge stage={b.stage} raw={b.rawState} /> : b.tracks.map((t) => <Badge key={t}>{trackLabel(t)}</Badge>)}
                {b.uploadedAtUtc && <span className="ml-auto text-xs text-faint">{formatDateTime(b.uploadedAtUtc)}</span>}
              </li>
            ))}
          </ul>
        )}
      </Panel>
    </div>
  );
}

/**
 * Il caricamento: un .ipa va ad App Store Connect (versione e build si leggono
 * dal file), un .aab a Google Play nel canale scelto. Il file arriva al server
 * e da lì il worker lo manda allo store: si può chiudere la pagina.
 */
function UploadPanel({ project }: { project: Project }) {
  const org = useOrg();
  const queryClient = useQueryClient();
  const stores = project.apps.map((a) => a.store);
  const [store, setStore] = useState<Store>(stores[0]);
  const [file, setFile] = useState<File | null>(null);
  const [track, setTrack] = useState("internal");
  const [status, setStatus] = useState("completed");
  const [rollout, setRollout] = useState("10");
  const [releaseName, setReleaseName] = useState("");
  const [notesLanguage, setNotesLanguage] = useState("it-IT");
  const [notes, setNotes] = useState("");
  const [progress, setProgress] = useState<number | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [done, setDone] = useState(false);
  const input = useRef<HTMLInputElement>(null);

  const accept = store === "AppStore" ? ".ipa" : ".aab";

  async function upload() {
    if (!file) return;
    setError(null);
    setDone(false);

    const form = new FormData();
    form.append("store", store);
    if (store === "GooglePlay") {
      form.append("track", track);
      form.append("releaseStatus", status);
      if (status === "inProgress") form.append("rolloutPercent", rollout);
      if (releaseName) form.append("releaseName", releaseName);
      if (notes) { form.append("releaseNotesLanguage", notesLanguage); form.append("releaseNotes", notes); }
    }
    form.append("file", file);

    // XMLHttpRequest e non fetch: serve l'avanzamento del caricamento, che
    // per un file da centinaia di MB è l'unica cosa che dice "sta andando".
    try {
      await uploadWithProgress(`/api/v1/orgs/${org.id}/projects/${project.id}/builds/uploads`, form, setProgress);
      setFile(null);
      setDone(true);
      await queryClient.invalidateQueries({ queryKey: keys.uploads(org.id, project.id) });
    } catch (e) {
      setError(e);
    } finally {
      setProgress(null);
    }
  }

  return (
    <Panel title="Carica una build" description="Il file arriva al server, poi parte verso lo store: puoi anche chiudere la pagina.">
      <div className="space-y-4 p-4">
        {stores.length > 1 && (
          <Segmented value={store} onChange={(s) => { setStore(s); setFile(null); }}
            options={stores.map((s) => ({ value: s, label: <><StoreGlyph store={s} className="size-3.5" />{storeName(s)}</> }))} />
        )}

        <button type="button" onClick={() => input.current?.click()}
          className="flex w-full items-center gap-3 rounded-md border border-dashed border-line-strong bg-field px-3 py-4 text-left hover:border-brand/50">
          {file ? <FileArchive className="size-5 text-brand-fg" /> : <CloudUpload className="size-5 text-faint" />}
          <span className="min-w-0 flex-1">
            <span className="block truncate text-[13px] text-fg">{file ? file.name : `Scegli il file ${accept}`}</span>
            <span className="block text-xs text-faint">
              {file ? `${(file.size / 1024 / 1024).toFixed(1)} MB` : store === "AppStore" ? "Versione e numero di build si leggono dal file." : "Android App Bundle firmato con la chiave di caricamento."}
            </span>
          </span>
          <input ref={input} type="file" accept={accept} className="hidden" onChange={(e) => { setFile(e.target.files?.[0] ?? null); e.target.value = ""; }} />
        </button>

        {store === "GooglePlay" && (
          <div className="grid gap-4 sm:grid-cols-3">
            <Field label="Canale">
              <Select value={track} onChange={(e) => setTrack(e.target.value)}>
                <option value="internal">Test interno</option>
                <option value="alpha">Test chiuso</option>
                <option value="beta">Test aperto</option>
                <option value="production">Produzione</option>
              </Select>
            </Field>
            <Field label="Rilascio">
              <Select value={status} onChange={(e) => setStatus(e.target.value)}>
                <option value="completed">A tutti</option>
                <option value="inProgress">Graduale</option>
                <option value="draft">Bozza (non rilasciare)</option>
              </Select>
            </Field>
            {status === "inProgress" ? (
              <Field label="Percentuale di utenti">
                <Input type="number" min={1} max={99} value={rollout} onChange={(e) => setRollout(e.target.value)} />
              </Field>
            ) : (
              <Field label="Nome della release (facoltativo)">
                <Input value={releaseName} onChange={(e) => setReleaseName(e.target.value)} placeholder="2.1.0" />
              </Field>
            )}
            <div className="sm:col-span-3">
              <Field label="Note di rilascio (facoltative)" hint={`${notes.length}/500 · lingua ${notesLanguage}`}>
                <div className="flex gap-2">
                  <div className="w-24 shrink-0"><Input value={notesLanguage} onChange={(e) => setNotesLanguage(e.target.value)} aria-label="Lingua delle note" /></div>
                  <Textarea rows={2} className="font-sans text-[13px]" maxLength={500} value={notes} onChange={(e) => setNotes(e.target.value)} />
                </div>
              </Field>
            </div>
          </div>
        )}

        {progress !== null && (
          <div className="space-y-1">
            <div className="h-1.5 overflow-hidden rounded-full bg-panel-2"><div className="h-full rounded-full bg-brand" style={{ width: `${progress}%` }} /></div>
            <p className="text-xs text-muted">Invio al server… {progress.toFixed(0)}%</p>
          </div>
        )}
        {error ? <Alert tone="bad">{errorMessage(error)}</Alert> : null}
        {done && <Alert tone="ok">In coda: lo stato si aggiorna qui sotto.</Alert>}

        <div className="flex justify-end">
          <Button variant="primary" icon={<CloudUpload className="size-3.5" />} disabled={!file || progress !== null} onClick={upload}>
            Carica su {storeName(store)}
          </Button>
        </div>
      </div>
    </Panel>
  );
}

/** Con il token come le altre chiamate: lo si prende dal client, rinnovandolo se serve. */
async function uploadWithProgress(url: string, form: FormData, onProgress: (p: number) => void) {
  const token = await currentAccessToken();

  await new Promise<void>((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open("POST", url);
    if (token) xhr.setRequestHeader("Authorization", `Bearer ${token}`);
    xhr.upload.onprogress = (e) => e.lengthComputable && onProgress((e.loaded / e.total) * 100);
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) return resolve();
      try {
        const body = JSON.parse(xhr.responseText);
        reject(new Error(body.detail ?? "Caricamento non riuscito."));
      } catch {
        reject(new Error(`Caricamento non riuscito (${xhr.status}).`));
      }
    };
    xhr.onerror = () => reject(new Error("Connessione interrotta durante il caricamento."));
    xhr.send(form);
  });
}

const STATUS: Record<BuildUploadItem["status"], { label: string; tone: "ok" | "warn" | "bad" | "neutral" | "brand" }> = {
  Queued: { label: "In coda", tone: "neutral" },
  Uploading: { label: "Invio allo store", tone: "brand" },
  Processing: { label: "Apple la sta elaborando", tone: "warn" },
  Completed: { label: "Fatto", tone: "ok" },
  Failed: { label: "Non riuscito", tone: "bad" },
};

function UploadHistory({ project }: { project: Project }) {
  const org = useOrg();
  const uploads = useBuildUploads(org.id, project.id);
  if (!uploads.data?.length) return null;

  return (
    <Panel title="Caricamenti dal pannello">
      <ul className="divide-y divide-line">
        {uploads.data.slice(0, 10).map((u) => (
          <li key={u.id} className="px-4 py-2.5">
            <div className="flex flex-wrap items-center gap-3">
              <StoreGlyph store={u.store} className={u.store === "AppStore" ? "text-ios" : "text-android"} />
              <span className="min-w-0 truncate text-[13px] text-fg">{u.fileName}</span>
              {(u.version || u.buildNumber) && <span className="font-mono text-xs text-muted">{u.version ?? ""}{u.buildNumber ? ` (${u.buildNumber})` : ""}</span>}
              {u.track && <Badge>{trackLabel(u.track)}</Badge>}
              <Badge tone={STATUS[u.status].tone}>{(u.status === "Uploading" || u.status === "Processing") && <Spinner className="size-3" />}{STATUS[u.status].label}</Badge>
              <span className="ml-auto text-xs text-faint">{formatDateTime(u.createdAtUtc)}</span>
            </div>
            {u.message && <p className={`mt-1 text-xs ${u.status === "Failed" ? "text-bad" : "text-muted"}`}>{u.message}</p>}
          </li>
        ))}
      </ul>
    </Panel>
  );
}
