import { useQueryClient } from "@tanstack/react-query";
import clsx from "clsx";
import { ExternalLink, Repeat, RotateCcw, Trash2 } from "lucide-react";
import { useState, type ReactNode } from "react";
import { Link } from "react-router";
import { errorMessage, request } from "../api/client";
import { keys } from "../api/hooks";
import { defaultPostOptions, type PostOptions, type Project, type SocialAccount, type SocialMediaItem, type SocialPost, type SocialPostStatus, type SocialTarget } from "../api/types";
import { canSeeProject, formatDateTime, useOrg } from "../components/org";
import { NetworkGlyph } from "../components/SocialIcons";
import { Alert, Badge, Button, Field, Input, Modal, Segmented, Textarea } from "../components/ui";
import { AccountPicker, accountsFor, MediaField, mediaHint, ProjectField, projectProblems } from "./EditorParts";
import { commercialIncomplete, NetworkOptions } from "./NetworkOptions";
import { accountLabel, countCharacters, networkName, optionProblems, problems } from "./rules";

type Tone = "neutral" | "ok" | "warn" | "bad" | "brand";

export const postStatus: Record<SocialPostStatus, { label: string; tone: Tone }> = {
  Draft: { label: "Bozza", tone: "neutral" },
  Scheduled: { label: "Programmato", tone: "brand" },
  Publishing: { label: "In uscita", tone: "warn" },
  Published: { label: "Pubblicato", tone: "ok" },
  PartiallyFailed: { label: "In parte non riuscito", tone: "warn" },
  Failed: { label: "Non riuscito", tone: "bad" },
  Inbox: { label: "Da programmare", tone: "neutral" },
};

const pad = (n: number) => String(n).padStart(2, "0");
const dateInput = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
const timeInput = (d: Date) => `${pad(d.getHours())}:${pad(d.getMinutes())}`;

/**
 * Scrivere, programmare e seguire un post. Lo stesso componente crea, modifica
 * e, quando il post è già uscito, mostra com'è andata su ogni account.
 */
export function PostEditor({ post, initialDate, defaultProjectId, toInbox, accounts, projects, admin, onClose }: {
  post?: SocialPost;
  /** Un post nuovo per la coda "Da programmare": account e data proposta facoltativi. */
  toInbox?: boolean;
  initialDate?: Date;
  /** Il progetto di un post nuovo (dal calendario di un progetto). */
  defaultProjectId?: string;
  accounts: SocialAccount[];
  projects: Project[];
  admin: boolean;
  onClose: () => void;
}) {
  const org = useOrg();
  const queryClient = useQueryClient();
  const readOnly = !admin || (post !== undefined && !post.editable);
  // Un post della coda parte dalla data proposta; senza, da fra un'ora.
  const start = post?.inbox
    ? (post.suggestedAtUtc ? new Date(post.suggestedAtUtc) : new Date(Math.ceil((Date.now() + 3_600_000) / 900_000) * 900_000))
    : post ? new Date(post.scheduledAtUtc) : initialDate ?? new Date(Date.now() + 3_600_000);

  const [text, setText] = useState(post?.text ?? "");
  const [selected, setSelected] = useState<string[]>(post?.targets.flatMap((t) => (t.accountId ? [t.accountId] : [])) ?? []);
  const [overrides, setOverrides] = useState<Record<string, string>>(
    Object.fromEntries(post?.targets.flatMap((t) => (t.accountId && t.textOverride !== null ? [[t.accountId, t.textOverride]] : [])) ?? []),
  );
  const [media, setMedia] = useState<SocialMediaItem[]>(post?.media ?? []);
  const [when, setWhen] = useState<"schedule" | "now">("schedule");
  // In coda la data è solo una proposta, e si può non darla.
  const queueMode = !!toInbox || !!post?.inbox;
  const [suggest, setSuggest] = useState(post?.inbox ? post.suggestedAtUtc !== null : false);
  const [date, setDate] = useState(dateInput(start));
  const [time, setTime] = useState(timeInput(start));
  // Chi vede solo alcuni progetti scrive sempre in uno dei suoi.
  const allowNone = canSeeProject(org, null);
  const [projectId, setProjectId] = useState(post?.projectId ?? defaultProjectId ?? (allowNone ? "" : projects[0]?.id ?? ""));
  const [uploading, setUploading] = useState(0);
  const [options, setOptions] = useState<PostOptions>(post?.options ?? defaultPostOptions);
  const [commercial, setCommercial] = useState(!!(post?.options.tikTokBrandOrganic || post?.options.tikTokBrandedContent));
  const [busy, setBusy] = useState<"draft" | "save" | "delete" | "retry" | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);

  const usable = accountsFor(accounts, projectId, selected);
  const chosen = accounts.filter((a) => selected.includes(a.id));
  const issues = chosen
    .map((a) => ({ account: a, problems: [...problems(overrides[a.id] ?? text, media, a.limits), ...optionProblems(a.network, options), ...projectProblems(a, projectId)] }))
    .filter((x) => x.problems.length > 0);
  const incomplete = chosen.some((a) => a.network === "TikTok") && commercialIncomplete(options, commercial);

  /** Un altro progetto: restano scelti solo gli account collegati anche a quello. */
  const changeProject = (id: string) => {
    setProjectId(id);
    if (id) setSelected((s) => s.filter((x) => accounts.find((a) => a.id === x)?.projectIds.includes(id)));
  };

  const toggle = (id: string) => setSelected((s) => (s.includes(id) ? s.filter((x) => x !== id) : [...s, id]));

  async function save(draft: boolean) {
    setBusy(draft ? "draft" : "save");
    setError(null);
    const scheduledAtUtc = when === "now" && !draft ? new Date().toISOString() : new Date(`${date}T${time}`).toISOString();
    const body = {
      text,
      scheduledAtUtc,
      isDraft: draft,
      // Salvato dalla coda resta in coda, con la data proposta (se c'è).
      inbox: draft && queueMode,
      suggestedAtUtc: draft && queueMode && suggest ? scheduledAtUtc : null,
      projectId: projectId || null,
      accountIds: selected,
      media: media.map((m) => ({ id: m.id, altText: m.altText || null })),
      overrides: Object.fromEntries(Object.entries(overrides).filter(([id, v]) => selected.includes(id) && v.trim())),
      options,
    };
    try {
      await request(post ? `/orgs/${org.id}/social/posts/${post.id}` : `/orgs/${org.id}/social/posts`, { method: post ? "PUT" : "POST", body });
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: keys.socialPosts(org.id) }),
        queryClient.invalidateQueries({ queryKey: keys.socialInbox(org.id) }),
      ]);
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
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: keys.socialPosts(org.id) }),
        queryClient.invalidateQueries({ queryKey: keys.socialInbox(org.id) }),
      ]);
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
          {toInbox ? (
            <Button variant="primary" loading={busy === "draft"} disabled={uploading > 0 || (!allowNone && !projectId) || (!text.trim() && media.length === 0)} onClick={() => save(true)}>
              Metti in coda
            </Button>
          ) : (
            <>
              <Button loading={busy === "draft"} disabled={uploading > 0 || (!allowNone && !projectId)} onClick={() => save(true)}>{post?.inbox ? "Salva in coda" : "Salva bozza"}</Button>
              <Button variant="primary" loading={busy === "save"} disabled={uploading > 0 || selected.length === 0 || issues.length > 0 || incomplete || (!allowNone && !projectId) || (queueMode && !suggest)}
                title={queueMode && !suggest ? "Per programmarlo scegli giorno e ora" : undefined} onClick={() => save(false)}>
                {when === "now" && !queueMode ? "Pubblica ora" : "Programma"}
              </Button>
            </>
          )}
        </>
      )}
    </div>
  );

  return (
    <Modal open onOpenChange={(o) => !o && onClose()} wide footer={footer}
      title={toInbox ? "Nuovo post da programmare" : post?.inbox ? "Post da programmare" : post ? (readOnly ? "Post" : "Modifica post") : "Nuovo post"}
      description={post && (post.imported
        ? <Badge tone="ok">Pubblicato fuori da ElephantSight</Badge>
        : post.inbox
          ? <span>Arrivato {post.source ? <>da <b className="text-fg">{post.source}</b></> : "con una chiave API"}{post.suggestedAtUtc ? `, proposto per il ${formatDateTime(post.suggestedAtUtc)}` : ", senza data proposta"}. Scegli gli account e programmalo.</span>
          : <span className="flex flex-wrap items-center gap-2">
              <Badge tone={postStatus[post.status].tone}>{postStatus[post.status].label}</Badge>
              {post.recurringPostId && (
                <Link to={`/o/${org.id}/social/recurring`} className="inline-flex items-center gap-1 text-xs text-brand-fg hover:underline">
                  <Repeat className="size-3" /> Uscita di un post ricorrente
                </Link>
              )}
            </span>)}>
      <div className="space-y-5">
        {post && post.targets.some((t) => t.status !== "Pending" || t.error) && <TargetList targets={post.targets} />}

        <ProjectField projects={projects} value={projectId} onChange={changeProject} allowNone={allowNone} readOnly={readOnly} />

        <Field label="Dove">
          <AccountPicker accounts={usable} selected={selected} onToggle={toggle} readOnly={readOnly} inProject={!!projectId} />
        </Field>

        <div className="space-y-2">
          <Field label="Testo">
            <Textarea rows={6} readOnly={readOnly} className="font-sans text-[0.8125rem]" value={text} onChange={(e) => setText(e.target.value)}
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

        <Field label="Immagini o video" hint={!readOnly ? mediaHint : undefined}>
          <MediaField media={media} onChange={setMedia} readOnly={readOnly} onUploading={setUploading} onError={setError} />
        </Field>

        <NetworkOptions accounts={chosen} media={media} options={options} onChange={setOptions} commercial={commercial} onCommercial={setCommercial} readOnly={readOnly} />

        {!readOnly && queueMode && (
          <div className="space-y-3">
            <label className="flex items-center gap-2 text-[0.8125rem] text-fg">
              <input type="checkbox" className="accent-brand" checked={suggest} onChange={(e) => setSuggest(e.target.checked)} />
              Proponi un giorno e un'ora
            </label>
            {suggest && (
              <div className="grid grid-cols-2 gap-3">
                <Field label="Giorno"><Input type="date" value={date} onChange={(e) => setDate(e.target.value)} /></Field>
                <Field label="Ora"><Input type="time" value={time} onChange={(e) => setTime(e.target.value)} /></Field>
              </div>
            )}
            <p className="text-xs text-muted">
              {toInbox
                ? "Resta nella coda Da programmare finché non scegli account e ora. Account e data sono facoltativi: puoi prepararlo adesso e decidere dopo."
                : "\"Salva in coda\" lo lascia in coda; \"Programma\" lo mette nel calendario all'ora scelta."}
            </p>
          </div>
        )}
        {!readOnly && !queueMode && (
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
          <p className="text-[0.8125rem] text-muted">
            {post.imported ? "Pubblicato il" : post.isDraft ? "Bozza per il" : "Programmato per il"} {formatDateTime(post.scheduledAtUtc)}
            {post.imported && " · importato dalla rete: si modifica o si cancella lì."}
          </p>
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
        <Textarea rows={3} readOnly={readOnly} className="mt-1.5 font-sans text-[0.8125rem]" value={override} onChange={(e) => onOverride(e.target.value)}
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
          <div className="flex flex-wrap items-center gap-2 text-[0.8125rem]">
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
