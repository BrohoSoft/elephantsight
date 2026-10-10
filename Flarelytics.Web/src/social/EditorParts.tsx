import clsx from "clsx";
import { ArrowLeft, ArrowRight, ImageOff, ImagePlus, Video, X } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { Link } from "react-router";
import { useInstance, useMe } from "../api/hooks";
import type { Project, SocialAccount, SocialMediaItem } from "../api/types";
import { canManageOrg, useOrg } from "../components/org";
import { NetworkGlyph } from "../components/SocialIcons";
import { Button, Field, Input, Select, Spinner } from "../components/ui";
import { accountLabel } from "./rules";
import { MediaPreview } from "./MediaPreview";
import { formatDuration, uploadMedia } from "./upload";

/*
 * I pezzi in comune fra l'editor di un post e quello di un post ricorrente:
 * la scelta degli account e le immagini o il video.
 */

/**
 * Gli account usabili in un post: senza progetto tutti, in un progetto quelli
 * collegati a quel progetto. Quelli già scelti restano visibili (per poterli
 * togliere) anche se non sono più collegati.
 */
export const accountsFor = (accounts: SocialAccount[], projectId: string, selected: string[]) =>
  projectId ? accounts.filter((a) => a.projectIds.includes(projectId) || selected.includes(a.id)) : accounts;

/** Il problema di un account fuori dal progetto del post (il server lo rifiuta anche in bozza). */
export const projectProblems = (account: SocialAccount, projectId: string) =>
  projectId && !account.projectIds.includes(projectId) ? ["non è collegato a questo progetto"] : [];

/**
 * Il progetto del post: viene prima di tutto, perché decide quali account si
 * possono usare. "Nessuno" (un post dell'organizzazione) solo per chi vede
 * tutti i progetti.
 */
export function ProjectField({ projects, value, onChange, allowNone, readOnly }: {
  projects: Project[];
  value: string;
  onChange: (projectId: string) => void;
  allowNone: boolean;
  readOnly: boolean;
}) {
  if (projects.length === 0 && allowNone) return null;
  return (
    <Field label="Progetto" hint="Il post compare nel calendario del progetto, e usa gli account collegati al progetto.">
      <Select disabled={readOnly} value={value} onChange={(e) => onChange(e.target.value)}>
        {allowNone ? <option value="">Nessuno (solo per bozze e coda: per programmarlo serve un progetto)</option> : !value && <option value="">Scegli il progetto</option>}
        {projects.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
      </Select>
    </Field>
  );
}

/** Gli account come pulsanti da accendere. Uno da ricollegare si può togliere ma non scegliere. */
export function AccountPicker({ accounts, selected, onToggle, readOnly, inProject }: {
  accounts: SocialAccount[];
  selected: string[];
  onToggle: (id: string) => void;
  readOnly: boolean;
  /** Il post è di un progetto: il messaggio senza account parla di collegarli al progetto. */
  inProject?: boolean;
}) {
  const org = useOrg();
  if (accounts.length === 0) {
    return (
      <p className="text-[0.8125rem] text-muted">
        {inProject ? "Nessun account collegato a questo progetto." : "Nessun account collegato."}{" "}
        {canManageOrg(org)
          ? <Link to={`/o/${org.id}/social/accounts`} className="text-brand-fg hover:underline">{inProject ? "Collegane uno al progetto" : "Collegane uno"}</Link>
          : "Chiedi a chi gestisce l'organizzazione di collegarne uno."}
      </p>
    );
  }

  return (
    <div className="flex flex-wrap gap-1.5">
      {accounts.map((a) => {
        const on = selected.includes(a.id);
        const broken = a.status === "NeedsReconnect";
        return (
          <button key={a.id} type="button" disabled={readOnly || (broken && !on)} onClick={() => onToggle(a.id)} aria-pressed={on}
            title={broken ? "Da ricollegare" : undefined}
            className={clsx(
              "inline-flex h-8 items-center gap-1.5 rounded-md border px-2.5 text-[0.8125rem] transition-colors disabled:cursor-not-allowed",
              on ? "border-brand/60 bg-brand/10 text-fg" : "border-line-strong bg-panel-2 text-muted hover:text-fg",
              broken && "opacity-60",
            )}>
            <NetworkGlyph network={a.network} className="size-3.5" />
            {accountLabel(a)}
          </button>
        );
      })}
    </div>
  );
}

/**
 * Le immagini o il video, con testo alternativo e ordine. I file si caricano
 * subito (le immagini già convertite in JPEG dal browser) e si attaccano al
 * post quando lo si salva.
 */
export function MediaField({ media, onChange, readOnly, onUploading, onError }: {
  media: SocialMediaItem[];
  onChange: (update: (media: SocialMediaItem[]) => SocialMediaItem[]) => void;
  readOnly: boolean;
  /** Quanti file si stanno caricando: finché non è zero non si salva. */
  onUploading: (count: number) => void;
  onError: (error: unknown) => void;
}) {
  const org = useOrg();
  const instance = useInstance();
  const me = useMe();
  const storageUnavailable = instance.data?.mediaStorage === "unavailable";
  const [uploading, setUploading] = useState(0);
  const [progress, setProgress] = useState<number | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);

  useEffect(() => onUploading(uploading), [uploading, onUploading]);
  const changeUploading = (delta: number) => setUploading((n) => n + delta);

  async function addFiles(files: FileList | null) {
    if (!files) return;
    onError(null);
    for (const file of Array.from(files)) {
      changeUploading(1);
      try {
        const item = await uploadMedia(org.id, file, setProgress);
        onChange((m) => [...m, { ...item, altText: "" }]);
      } catch (e) {
        onError(e);
      } finally {
        changeUploading(-1);
        setProgress(null);
      }
    }
  }

  const move = (i: number, delta: number) =>
    onChange((m) => {
      const next = [...m];
      [next[i], next[i + delta]] = [next[i + delta], next[i]];
      return next;
    });

  return (
    <div className="space-y-2">
      {media.map((m, i) => (
        <div key={m.id} className="flex items-start gap-3 rounded-md border border-line bg-panel-2/50 p-2">
          <MediaPreview item={m} player className={clsx("shrink-0 rounded", m.kind === "Video" && m.url ? "h-28 w-16" : "size-16")} />
          <div className="min-w-0 flex-1 space-y-1.5">
            <Input readOnly={readOnly} value={m.altText ?? ""} placeholder="Testo alternativo" aria-label={`Testo alternativo dell'immagine ${i + 1}`}
              onChange={(e) => onChange((list) => list.map((x) => (x.id === m.id ? { ...x, altText: e.target.value } : x)))} />
            <p className="text-[0.6875rem] text-faint">
              {m.kind === "Video" ? <><Video className="mr-1 inline size-3" />{formatDuration(m.durationMs ?? 0)} · </> : null}
              {m.width}×{m.height} · {m.sizeBytes > 1024 * 1024 ? `${(m.sizeBytes / 1024 / 1024).toFixed(1)} MB` : `${Math.round(m.sizeBytes / 1024)} KB`}
            </p>
            {m.originalDeleted && (
              <p className="flex items-start gap-1 text-[0.6875rem] text-faint">
                <ImageOff className="mt-px size-3 shrink-0" />
                Il file originale è stato cancellato qualche giorno dopo la pubblicazione: qui resta {m.thumbnailUrl ? "la miniatura" : "solo il nome"}, il post si vede dal suo link sulla rete.
              </p>
            )}
          </div>
          {!readOnly && (
            <div className="flex shrink-0 gap-0.5">
              <Button size="sm" variant="ghost" disabled={i === 0} onClick={() => move(i, -1)} icon={<ArrowLeft className="size-3" />} aria-label="Sposta prima" />
              <Button size="sm" variant="ghost" disabled={i === media.length - 1} onClick={() => move(i, 1)} icon={<ArrowRight className="size-3" />} aria-label="Sposta dopo" />
              <Button size="sm" variant="ghost" onClick={() => onChange((list) => list.filter((x) => x.id !== m.id))} icon={<X className="size-3" />} aria-label="Togli" />
            </div>
          )}
        </div>
      ))}
      {!readOnly && storageUnavailable && (
        <div className="flex items-start gap-3 rounded-md border border-dashed border-line-strong bg-field px-3 py-3 text-[0.8125rem] text-muted">
          <ImageOff className="size-5 shrink-0 text-faint" />
          <span>
            Il caricamento di immagini e video non è disponibile: lo storage dei file non è configurato. I post di solo testo si programmano lo stesso.
            {me.data?.isInstanceAdmin && !instance.data?.mediaStorageLocked
              ? <> Configuralo in <Link to="/instance" className="text-brand hover:underline">Impostazioni dell'istanza → Storage dei file</Link>.</>
              : instance.data?.mediaStorageLocked ? " Lo configura chi gestisce l'installazione." : " Avvisa un amministratore dell'istanza."}
          </span>
        </div>
      )}
      {!readOnly && !storageUnavailable && (
        <button type="button" onClick={() => fileInput.current?.click()} disabled={uploading > 0}
          onDragOver={(e) => e.preventDefault()} onDrop={(e) => { e.preventDefault(); addFiles(e.dataTransfer.files); }}
          className="flex w-full items-center gap-3 rounded-md border border-dashed border-line-strong bg-field px-3 py-3 text-left hover:border-brand/50">
          {uploading > 0 ? <Spinner /> : <ImagePlus className="size-5 text-faint" />}
          <span className="text-[0.8125rem] text-muted">
            {uploading > 0 ? `Caricamento…${progress !== null && progress < 1 ? ` ${Math.round(progress * 100)}%` : ""}` : "Aggiungi immagini o un video (o trascinali qui)"}
          </span>
          <input ref={fileInput} type="file" accept="image/*,video/mp4,video/quicktime" multiple className="hidden" onChange={(e) => { addFiles(e.target.files); e.target.value = ""; }} />
        </button>
      )}
    </div>
  );
}

export const mediaHint =
  "Le immagini si convertono in JPEG sotto 1 MB. Un video (MP4 o MOV) va da solo: su Instagram diventa un Reel, su TikTok e Threads un video. Il testo alternativo aiuta chi usa un lettore di schermo.";
