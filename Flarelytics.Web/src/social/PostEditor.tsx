import { useQueryClient } from "@tanstack/react-query";
import clsx from "clsx";
import { ArrowLeft, ArrowRight, ExternalLink, ImagePlus, RotateCcw, Trash2, X } from "lucide-react";
import { useRef, useState, type ReactNode } from "react";
import { Link } from "react-router";
import { errorMessage, request } from "../api/client";
import { keys } from "../api/hooks";
import type { Project, SocialAccount, SocialMediaItem, SocialPost, SocialPostStatus, SocialTarget } from "../api/types";
import { formatDateTime, useOrg } from "../components/org";
import { NetworkGlyph } from "../components/SocialIcons";
import { Alert, Badge, Button, Field, Input, Modal, Segmented, Select, Spinner, Textarea } from "../components/ui";
import { toJpeg } from "./image";
import { accountLabel, countCharacters, networkName, problems } from "./rules";

type Tone = "neutral" | "ok" | "warn" | "bad" | "brand";

export const postStatus: Record<SocialPostStatus, { label: string; tone: Tone }> = {
  Draft: { label: "Bozza", tone: "neutral" },
  Scheduled: { label: "Programmato", tone: "brand" },
  Publishing: { label: "In uscita", tone: "warn" },
  Published: { label: "Pubblicato", tone: "ok" },
  PartiallyFailed: { label: "In parte non riuscito", tone: "warn" },
  Failed: { label: "Non riuscito", tone: "bad" },
};

const pad = (n: number) => String(n).padStart(2, "0");
const dateInput = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
const timeInput = (d: Date) => `${pad(d.getHours())}:${pad(d.getMinutes())}`;

/**
 * Scrivere, programmare e seguire un post. Lo stesso componente crea, modifica
 * e, quando il post è già uscito, mostra com'è andata su ogni account.
 */
export function PostEditor({ post, initialDate, accounts, projects, admin, onClose }: {
  post?: SocialPost;
  initialDate?: Date;
  accounts: SocialAccount[];
  projects: Project[];
  admin: boolean;
  onClose: () => void;
}) {
  const org = useOrg();
  const queryClient = useQueryClient();
  const readOnly = !admin || (post !== undefined && !post.editable);
  const start = post ? new Date(post.scheduledAtUtc) : initialDate ?? new Date(Date.now() + 3_600_000);

  const [text, setText] = useState(post?.text ?? "");
  const [selected, setSelected] = useState<string[]>(post?.targets.flatMap((t) => (t.accountId ? [t.accountId] : [])) ?? []);
  const [overrides, setOverrides] = useState<Record<string, string>>(
    Object.fromEntries(post?.targets.flatMap((t) => (t.accountId && t.textOverride !== null ? [[t.accountId, t.textOverride]] : [])) ?? []),
  );
  const [media, setMedia] = useState<SocialMediaItem[]>(post?.media ?? []);
  const [when, setWhen] = useState<"schedule" | "now">("schedule");
  const [date, setDate] = useState(dateInput(start));
  const [time, setTime] = useState(timeInput(start));
  const [projectId, setProjectId] = useState(post?.projectId ?? "");
  const [uploading, setUploading] = useState(0);
  const [busy, setBusy] = useState<"draft" | "save" | "delete" | "retry" | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);

  const chosen = accounts.filter((a) => selected.includes(a.id));
  const issues = chosen.map((a) => ({ account: a, problems: problems(overrides[a.id] ?? text, media, a.limits) })).filter((x) => x.problems.length > 0);

  const toggle = (id: string) => setSelected((s) => (s.includes(id) ? s.filter((x) => x !== id) : [...s, id]));

  async function addFiles(files: FileList | null) {
    if (!files) return;
    setError(null);
    for (const file of Array.from(files)) {
      setUploading((n) => n + 1);
      try {
        const form = new FormData();
        form.append("file", await toJpeg(file), file.name.replace(/\.[^.]+$/, "") + ".jpg");
        const item = await request<SocialMediaItem>(`/orgs/${org.id}/social/media`, { method: "POST", body: form });
        setMedia((m) => [...m, { ...item, altText: "" }]);
      } catch (e) {
        setError(e);
      } finally {
        setUploading((n) => n - 1);
      }
    }
  }

  const move = (i: number, delta: number) =>
    setMedia((m) => {
      const next = [...m];
      [next[i], next[i + delta]] = [next[i + delta], next[i]];
      return next;
    });

  async function save(draft: boolean) {
    setBusy(draft ? "draft" : "save");
    setError(null);
    const scheduledAtUtc = when === "now" && !draft ? new Date().toISOString() : new Date(`${date}T${time}`).toISOString();
    const body = {
      text,
      scheduledAtUtc,
      isDraft: draft,
      projectId: projectId || null,
      accountIds: selected,
      media: media.map((m) => ({ id: m.id, altText: m.altText || null })),
      overrides: Object.fromEntries(Object.entries(overrides).filter(([id, v]) => selected.includes(id) && v.trim())),
    };
    try {
      await request(post ? `/orgs/${org.id}/social/posts/${post.id}` : `/orgs/${org.id}/social/posts`, { method: post ? "PUT" : "POST", body });
      await queryClient.invalidateQueries({ queryKey: keys.socialPosts(org.id) });
      onClose();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(null);
    }
  }

  async function act(kind: "delete" | "retry") {
    setBusy(kind);
    setError(null);
    try {
      await request(`/orgs/${org.id}/social/posts/${post!.id}${kind === "retry" ? "/retry" : ""}`, { method: kind === "retry" ? "POST" : "DELETE" });
      await queryClient.invalidateQueries({ queryKey: keys.socialPosts(org.id) });
      onClose();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(null);
    }
  }

  const hasFailures = post?.targets.some((t) => t.status === "Failed") ?? false;
  const footer = (
    <div className="flex w-full flex-wrap items-center gap-2">
      {post && admin && !post.imported && (confirmDelete ? (
        <>
          <Button variant="danger" loading={busy === "delete"} onClick={() => act("delete")}>Elimina dal calendario</Button>
          <Button variant="ghost" onClick={() => setConfirmDelete(false)}>No</Button>
        </>
      ) : <Button variant="ghost" icon={<Trash2 className="size-3.5" />} onClick={() => setConfirmDelete(true)} aria-label="Elimina" />)}
      <span className="flex-1" />
      {readOnly ? (
        <>
          {admin && hasFailures && <Button icon={<RotateCcw className="size-3.5" />} loading={busy === "retry"} onClick={() => act("retry")}>Riprova i non riusciti</Button>}
          <Button variant="primary" onClick={onClose}>Chiudi</Button>
        </>
      ) : (
        <>
          <Button variant="ghost" onClick={onClose}>Annulla</Button>
          <Button loading={busy === "draft"} disabled={uploading > 0} onClick={() => save(true)}>Salva bozza</Button>
          <Button variant="primary" loading={busy === "save"} disabled={uploading > 0 || selected.length === 0 || issues.length > 0} onClick={() => save(false)}>
            {when === "now" ? "Pubblica ora" : "Programma"}
          </Button>
        </>
      )}
    </div>
  );

  return (
    <Modal open onOpenChange={(o) => !o && onClose()} wide footer={footer}
      title={post ? (readOnly ? "Post" : "Modifica post") : "Nuovo post"}
      description={post && (post.imported
        ? <Badge tone="ok">Pubblicato fuori da WatchStore</Badge>
        : <Badge tone={postStatus[post.status].tone}>{postStatus[post.status].label}</Badge>)}>
      <div className="space-y-5">
        {post && post.targets.some((t) => t.status !== "Pending" || t.error) && <TargetList targets={post.targets} />}

        <Field label="Dove">
          {accounts.length === 0 ? (
            <p className="text-[13px] text-muted">
              Nessun account collegato. <Link to={`/o/${org.id}/social/accounts`} className="text-brand-fg hover:underline">Collegane uno</Link>.
            </p>
          ) : (
            <div className="flex flex-wrap gap-1.5">
              {accounts.map((a) => {
                const on = selected.includes(a.id);
                const broken = a.status === "NeedsReconnect";
                return (
                  <button key={a.id} type="button" disabled={readOnly || (broken && !on)} onClick={() => toggle(a.id)} aria-pressed={on}
                    title={broken ? "Da ricollegare" : undefined}
                    className={clsx(
                      "inline-flex h-8 items-center gap-1.5 rounded-md border px-2.5 text-[13px] transition-colors disabled:cursor-not-allowed",
                      on ? "border-brand/60 bg-brand/10 text-fg" : "border-line-strong bg-panel-2 text-muted hover:text-fg",
                      broken && "opacity-60",
                    )}>
                    <NetworkGlyph network={a.network} className="size-3.5" />
                    {accountLabel(a)}
                  </button>
                );
              })}
            </div>
          )}
        </Field>

        <div className="space-y-2">
          <Field label="Testo">
            <Textarea rows={6} readOnly={readOnly} className="font-sans text-[13px]" value={text} onChange={(e) => setText(e.target.value)}
              placeholder="Cosa vuoi raccontare? Link e #hashtag funzionano su tutte le reti." />
          </Field>
          {chosen.length > 0 && (
            <ul className="space-y-1.5">
              {chosen.map((a) => (
                <AccountText key={a.id} account={a} text={text} override={overrides[a.id]} readOnly={readOnly}
                  issues={issues.find((x) => x.account.id === a.id)?.problems ?? []}
                  onOverride={(v) => setOverrides((o) => {
                    const next = { ...o };
                    if (v === undefined) delete next[a.id];
                    else next[a.id] = v;
                    return next;
                  })} />
              ))}
            </ul>
          )}
        </div>

        <Field label="Immagini" hint={!readOnly ? "Si convertono in JPEG sotto 1 MB, come vogliono Instagram e Bluesky. Il testo alternativo aiuta chi usa un lettore di schermo." : undefined}>
          <div className="space-y-2">
            {media.map((m, i) => (
              <div key={m.id} className="flex items-start gap-3 rounded-md border border-line bg-panel-2/50 p-2">
                <img src={m.url} alt={m.altText ?? ""} className="size-16 shrink-0 rounded object-cover" />
                <div className="min-w-0 flex-1 space-y-1.5">
                  <Input readOnly={readOnly} value={m.altText ?? ""} placeholder="Testo alternativo" aria-label={`Testo alternativo dell'immagine ${i + 1}`}
                    onChange={(e) => setMedia((list) => list.map((x) => (x.id === m.id ? { ...x, altText: e.target.value } : x)))} />
                  <p className="text-[11px] text-faint">{m.width}×{m.height} · {Math.round(m.sizeBytes / 1024)} KB</p>
                </div>
                {!readOnly && (
                  <div className="flex shrink-0 gap-0.5">
                    <Button size="sm" variant="ghost" disabled={i === 0} onClick={() => move(i, -1)} icon={<ArrowLeft className="size-3" />} aria-label="Sposta prima" />
                    <Button size="sm" variant="ghost" disabled={i === media.length - 1} onClick={() => move(i, 1)} icon={<ArrowRight className="size-3" />} aria-label="Sposta dopo" />
                    <Button size="sm" variant="ghost" onClick={() => setMedia((list) => list.filter((x) => x.id !== m.id))} icon={<X className="size-3" />} aria-label="Togli" />
                  </div>
                )}
              </div>
            ))}
            {!readOnly && (
              <button type="button" onClick={() => fileInput.current?.click()} disabled={uploading > 0}
                onDragOver={(e) => e.preventDefault()} onDrop={(e) => { e.preventDefault(); addFiles(e.dataTransfer.files); }}
                className="flex w-full items-center gap-3 rounded-md border border-dashed border-line-strong bg-field px-3 py-3 text-left hover:border-brand/50">
                {uploading > 0 ? <Spinner /> : <ImagePlus className="size-5 text-faint" />}
                <span className="text-[13px] text-muted">{uploading > 0 ? "Caricamento…" : "Aggiungi immagini (o trascinale qui)"}</span>
                <input ref={fileInput} type="file" accept="image/*" multiple className="hidden" onChange={(e) => { addFiles(e.target.files); e.target.value = ""; }} />
              </button>
            )}
          </div>
        </Field>

        {!readOnly && (
          <div className="space-y-3">
            <Segmented value={when} onChange={setWhen} options={[{ value: "schedule", label: "Programma" }, { value: "now", label: "Pubblica subito" }]} />
            {when === "schedule" && (
              <div className="grid grid-cols-2 gap-3">
                <Field label="Giorno"><Input type="date" value={date} onChange={(e) => setDate(e.target.value)} /></Field>
                <Field label="Ora"><Input type="time" value={time} onChange={(e) => setTime(e.target.value)} /></Field>
              </div>
            )}
          </div>
        )}
        {readOnly && post && (
          <p className="text-[13px] text-muted">
            {post.imported ? "Pubblicato il" : post.isDraft ? "Bozza per il" : "Programmato per il"} {formatDateTime(post.scheduledAtUtc)}
            {post.imported && " · importato dalla rete: si modifica o si cancella lì."}
          </p>
        )}

        {projects.length > 0 && (
          <Field label="Progetto (facoltativo)" hint="Per filtrare il calendario per app.">
            <Select disabled={readOnly} value={projectId} onChange={(e) => setProjectId(e.target.value)}>
              <option value="">Nessuno</option>
              {projects.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
            </Select>
          </Field>
        )}

        {error ? <Alert tone="bad">{errorMessage(error)}</Alert> : null}
      </div>
    </Modal>
  );
}

/** Il contatore di un account sotto il testo, con la possibilità di scrivere un testo solo per lui. */
function AccountText({ account: a, text, override, issues, readOnly, onOverride }: {
  account: SocialAccount;
  text: string;
  override: string | undefined;
  issues: string[];
  readOnly: boolean;
  onOverride: (value: string | undefined) => void;
}) {
  const count = countCharacters(override ?? text, a.limits);
  return (
    <li className="rounded-md border border-line px-2.5 py-1.5">
      <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs">
        <NetworkGlyph network={a.network} className="size-3.5 text-muted" />
        <span className="text-fg">{accountLabel(a)}</span>
        <span className={clsx("font-mono", count > a.limits.maxCharacters ? "text-bad" : "text-faint")}>{count}/{a.limits.maxCharacters}</span>
        {issues.length > 0 && <span className="text-bad">{issues.join(" · ")}</span>}
        <span className="flex-1" />
        {!readOnly && (override === undefined ? (
          <button type="button" className="text-brand-fg hover:underline" onClick={() => onOverride(text)}>Testo diverso</button>
        ) : (
          <button type="button" className="text-muted hover:text-fg" onClick={() => onOverride(undefined)}>Usa il testo comune</button>
        ))}
      </div>
      {override !== undefined && (
        <Textarea rows={3} readOnly={readOnly} className="mt-1.5 font-sans text-[13px]" value={override} onChange={(e) => onOverride(e.target.value)}
          aria-label={`Testo per ${networkName[a.network]}`} />
      )}
    </li>
  );
}

const targetStatus = (t: SocialTarget): ReactNode => {
  switch (t.status) {
    case "Published":
      return <Badge tone="ok">Pubblicato</Badge>;
    case "Publishing":
      return <Badge tone="warn">In uscita</Badge>;
    case "Failed":
      return <Badge tone="bad">Non riuscito</Badge>;
    default:
      return t.nextAttemptAtUtc ? <Badge tone="warn">Riprova alle {formatDateTime(t.nextAttemptAtUtc)}</Badge> : <Badge>In attesa</Badge>;
  }
};

/** Com'è andata su ogni account: link al post uscito, o il motivo dell'errore. */
function TargetList({ targets }: { targets: SocialTarget[] }) {
  return (
    <ul className="divide-y divide-line rounded-md border border-line">
      {targets.map((t) => (
        <li key={t.id} className="space-y-1 px-3 py-2">
          <div className="flex flex-wrap items-center gap-2 text-[13px]">
            <NetworkGlyph network={t.network} className="size-3.5 text-muted" />
            <span className="text-fg">{t.accountName}</span>
            {!t.accountId && <span className="text-xs text-faint">(scollegato)</span>}
            {targetStatus(t)}
            <span className="flex-1" />
            {t.externalUrl && (
              <a href={t.externalUrl} target="_blank" rel="noreferrer" className="inline-flex items-center gap-1 text-xs text-brand-fg hover:underline">
                Apri <ExternalLink className="size-3" />
              </a>
            )}
          </div>
          {t.error && <p className="text-xs text-muted">{t.error}</p>}
        </li>
      ))}
    </ul>
  );
}
