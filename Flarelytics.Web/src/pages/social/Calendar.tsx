import clsx from "clsx";
import { CalendarDays, ChevronLeft, ChevronRight, ListChecks, Plus, Repeat } from "lucide-react";
import { useMemo, useState } from "react";
import { Link } from "react-router";
import { canAdmin, useProjects, useRecurringOccurrences, useSocialAccounts, useSocialPosts, useSocialRecurring } from "../../api/hooks";
import type { Project, RecurringPost, SocialAccount, SocialPost } from "../../api/types";
import { canManageOrg, useOrg } from "../../components/org";
import { NetworkGlyph } from "../../components/SocialIcons";
import { Badge, Button, EmptyState, Modal, PageHeader, PageLoader, Select } from "../../components/ui";
import { BulkAccounts } from "../../social/BulkAccounts";
import { PostEditor, postStatus } from "../../social/PostEditor";
import { RecurringEditor } from "../../social/RecurringEditor";

const monthFormat = new Intl.DateTimeFormat("it-IT", { month: "long", year: "numeric" });
const timeFormat = new Intl.DateTimeFormat("it-IT", { hour: "2-digit", minute: "2-digit" });
const dayFormat = new Intl.DateTimeFormat("it-IT", { weekday: "long", day: "numeric", month: "long" });
const WEEKDAYS = ["lun", "mar", "mer", "gio", "ven", "sab", "dom"];
/** Quanti post ci stanno in una casella; gli altri si vedono aprendo il giorno. */
const PER_DAY = 3;

const sameDay = (a: Date, b: Date) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
const dayKey = (d: Date) => `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`;

/** Le sei settimane che contengono il mese, da lunedì: la griglia ha sempre la stessa altezza. */
function monthGrid(month: Date): Date[] {
  const first = new Date(month.getFullYear(), month.getMonth(), 1);
  const offset = (first.getDay() + 6) % 7; // lunedì = 0
  return Array.from({ length: 42 }, (_, i) => new Date(first.getFullYear(), first.getMonth(), 1 - offset + i));
}

const chipTone: Record<SocialPost["status"], string> = {
  Draft: "border-l-line-strong bg-panel-2 text-muted",
  Scheduled: "border-l-brand bg-brand/10 text-fg",
  Publishing: "border-l-warn bg-warn/10 text-fg",
  Published: "border-l-ok bg-ok/10 text-fg",
  PartiallyFailed: "border-l-warn bg-warn/10 text-fg",
  Failed: "border-l-bad bg-bad/10 text-fg",
  Inbox: "border-l-line-strong bg-panel-2 text-muted", // non sta sul calendario, ma il tipo lo vuole
};

/** Una casella del calendario: un post vero, o un'uscita futura di un post ricorrente (non ancora un post). */
type Entry = { kind: "post"; at: Date; post: SocialPost } | { kind: "next"; at: Date; recurring: RecurringPost };

/**
 * Il calendario dei post: il mese, con i post di ogni giorno. Un clic su un
 * giorno vuoto apre un post nuovo per quel giorno, un clic su un post lo apre.
 * Sul telefono la griglia diventa un elenco dei giorni con qualcosa.
 *
 * Fuori dai progetti mostra tutto quello che il membro vede (con un filtro per
 * progetto); dentro un progetto (<code>project</code>) solo i post di quel
 * progetto, e i post nuovi nascono lì.
 */
export function SocialCalendarPage({ project }: { project?: Project } = {}) {
  const org = useOrg();
  const admin = canAdmin(org.role);
  const [month, setMonth] = useState(() => new Date(new Date().getFullYear(), new Date().getMonth(), 1));
  const [projectId, setProjectId] = useState(project?.id ?? "");
  const [editing, setEditing] = useState<{ post?: SocialPost; date?: Date } | null>(null);
  // Un post ricorrente da modificare, o (template) la copia da cui crearne uno nuovo.
  const [editingRecurring, setEditingRecurring] = useState<{ recurring?: RecurringPost; template?: RecurringPost } | null>(null);
  // Modalità selezione: i clic sui post li selezionano invece di aprirli.
  const [selecting, setSelecting] = useState(false);
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  // Il giorno aperto per intero, quando ha più post di quelli che stanno nella casella.
  const [openDay, setOpenDay] = useState<Date | null>(null);

  const days = useMemo(() => monthGrid(month), [month]);
  const from = days[0];
  const to = new Date(days[41].getFullYear(), days[41].getMonth(), days[41].getDate() + 1);
  const posts = useSocialPosts(org.id, from, to, projectId || undefined);
  const occurrences = useRecurringOccurrences(org.id, from, to, projectId || undefined);
  const recurring = useSocialRecurring(org.id);
  const accounts = useSocialAccounts(org.id);
  const projects = useProjects(org.id);

  // I post e le prossime uscite dei post ricorrenti, in ordine di ora.
  const byDay = useMemo(() => {
    const entries: Entry[] = (posts.data ?? []).map((p) => ({ kind: "post" as const, at: new Date(p.scheduledAtUtc), post: p }));
    for (const o of occurrences.data ?? []) {
      const r = recurring.data?.find((x) => x.id === o.recurringPostId);
      if (r) entries.push({ kind: "next", at: new Date(o.atUtc), recurring: r });
    }
    entries.sort((a, b) => a.at.getTime() - b.at.getTime());
    const map = new Map<string, Entry[]>();
    for (const e of entries) map.set(dayKey(e.at), [...(map.get(dayKey(e.at)) ?? []), e]);
    return map;
  }, [posts.data, occurrences.data, recurring.data]);

  if (accounts.isPending || projects.isPending) return <PageLoader />;

  const today = new Date();
  const shift = (delta: number) => setMonth((m) => new Date(m.getFullYear(), m.getMonth() + delta, 1));
  /** Un giorno nuovo parte alle 10, oggi fra un'ora: mai nel passato. */
  const newPostAt = (day: Date) => {
    const at = new Date(day.getFullYear(), day.getMonth(), day.getDate(), 10, 0);
    return at.getTime() < Date.now() ? new Date(Math.ceil((Date.now() + 3_600_000) / 900_000) * 900_000) : at;
  };
  const open = (post?: SocialPost, date?: Date) => setEditing({ post, date });
  /** Si possono cambiare solo i post non ancora usciti, e non quelli importati. */
  const selectable = (p: SocialPost) => p.editable && !p.imported;
  const toggle = (p: SocialPost) => setSelectedIds((ids) => (ids.includes(p.id) ? ids.filter((x) => x !== p.id) : [...ids, p.id]));
  const onChip = (p: SocialPost) => (selecting ? selectable(p) && toggle(p) : open(p));
  const monthSelectable = (posts.data ?? []).filter((p) => selectable(p) && new Date(p.scheduledAtUtc).getMonth() === month.getMonth());
  const selected = (posts.data ?? []).filter((p) => selectedIds.includes(p.id));
  const stopSelecting = () => { setSelecting(false); setSelectedIds([]); };

  const visibleDays = days.filter((d) => d.getMonth() === month.getMonth() && byDay.has(dayKey(d)));

  return (
    <>
      <PageHeader
        title={project ? `Calendario social · ${project.name}` : "Calendario social"}
        description={project
          ? "I post di questo progetto: quelli programmati qui, le prossime uscite dei post ricorrenti e quelli usciti da altre app (tratteggiati). Il calendario di tutta l'organizzazione è in Social → Calendario."
          : "I post di tutti i progetti che vedi: quelli programmati qui, che ElephantSight pubblica all'ora indicata, le prossime uscite dei post ricorrenti e quelli usciti da altre app negli ultimi 90 giorni (tratteggiati)."}
        actions={
          <>
            {!project && (projects.data?.length ?? 0) > 0 && (
              <Select value={projectId} onChange={(e) => setProjectId(e.target.value)} className="w-44" aria-label="Progetto">
                <option value="">Tutti i progetti</option>
                {projects.data!.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
              </Select>
            )}
            {admin && !selecting && <Button icon={<ListChecks className="size-3.5" />} onClick={() => setSelecting(true)}>Cambia account a più post</Button>}
            {admin && <Button variant="primary" icon={<Plus className="size-3.5" />} onClick={() => open(undefined, newPostAt(today))}>Nuovo post</Button>}
          </>
        }
      />

      {(project ? !accounts.data!.some((a) => a.projectIds.includes(project.id)) : accounts.data!.length === 0) && (
        <div className="mb-6 rounded-lg border border-dashed border-line-strong">
          <EmptyState icon={<CalendarDays className="size-5" />} title={project ? "Nessun account social collegato a questo progetto" : "Nessun account social collegato"}
            action={canManageOrg(org) && <Link to={`/o/${org.id}/social/accounts`}><Button variant="primary">{project ? "Collega un account al progetto" : "Collega un account"}</Button></Link>}>
            {project
              ? "Un account si collega a uno o più progetti da Account social: lo stesso account può servire a più progetti, con post diversi."
              : "Bluesky, Mastodon, Instagram, Pagine Facebook, TikTok e Threads. Puoi già scrivere bozze, ma per pubblicare serve almeno un account."}
          </EmptyState>
        </div>
      )}

      {selecting && <BulkAccounts selected={selected} accounts={accounts.data!} onDone={stopSelecting} />}

      <div className="mb-3 flex items-center gap-2">
        <Button size="sm" variant="ghost" icon={<ChevronLeft className="size-4" />} aria-label="Mese prima" onClick={() => shift(-1)} />
        <Button size="sm" variant="ghost" icon={<ChevronRight className="size-4" />} aria-label="Mese dopo" onClick={() => shift(1)} />
        <h2 className="text-sm font-medium text-fg first-letter:uppercase">{monthFormat.format(month)}</h2>
        <Button size="sm" className="ml-1" onClick={() => setMonth(new Date(today.getFullYear(), today.getMonth(), 1))}>Oggi</Button>
        {posts.isFetching && <span className="text-xs text-faint">aggiornamento…</span>}
        {selecting && (
          <span className="ml-auto flex items-center gap-2 text-xs text-muted">
            Clicca i post per selezionarli
            <Button size="sm" onClick={() => setSelectedIds(monthSelectable.map((p) => p.id))} disabled={monthSelectable.length === 0}>Seleziona tutti del mese</Button>
            {selectedIds.length > 0 && <Button size="sm" variant="ghost" onClick={() => setSelectedIds([])}>Deseleziona</Button>}
          </span>
        )}
      </div>

      {/* Dal tablet in su: la griglia del mese. */}
      <div className="hidden overflow-hidden rounded-lg border border-line bg-panel md:block">
        <div className="grid grid-cols-7 border-b border-line bg-panel-2/50">
          {WEEKDAYS.map((d) => <div key={d} className="px-2 py-1.5 text-[0.6875rem] font-medium tracking-wide text-faint uppercase">{d}</div>)}
        </div>
        <div className="grid grid-cols-7">
          {days.map((day, i) => {
            const inMonth = day.getMonth() === month.getMonth();
            const list = byDay.get(dayKey(day)) ?? [];
            return (
              <div key={i}
                onClick={() => admin && !selecting && open(undefined, newPostAt(day))}
                className={clsx(
                  "group min-h-28 border-line p-1.5 [&:not(:nth-child(7n))]:border-r [&:nth-child(n+8)]:border-t",
                  !inMonth && "bg-panel-2/40",
                  admin && !selecting && "cursor-pointer hover:bg-hover/40",
                )}>
                <div className="mb-1 flex items-center justify-between">
                  <span className={clsx(
                    "flex size-6 items-center justify-center rounded-full text-xs",
                    sameDay(day, today) ? "bg-brand font-medium text-brand-ink" : inMonth ? "text-muted" : "text-faint",
                  )}>{day.getDate()}</span>
                  {admin && <Plus className="size-3.5 text-faint opacity-0 group-hover:opacity-100" />}
                </div>
                <div className="space-y-1">
                  {(selecting ? list : list.slice(0, PER_DAY)).map((e) => e.kind === "post" ? (
                    <PostChip key={e.post.id} post={e.post} onOpen={() => onChip(e.post)} selecting={selecting} selected={selectedIds.includes(e.post.id)} disabled={selecting && !selectable(e.post)} />
                  ) : (
                    <NextChip key={`${e.recurring.id}-${e.at.getTime()}`} entry={e} accounts={accounts.data!} disabled={selecting} onOpen={() => setEditingRecurring({ recurring: e.recurring })} />
                  ))}
                  {!selecting && list.length > PER_DAY && (
                    <button type="button" onClick={(ev) => { ev.stopPropagation(); setOpenDay(day); }}
                      className="w-full rounded px-1 py-0.5 text-left text-[0.6875rem] font-medium text-brand-fg hover:bg-hover">
                      +{list.length - PER_DAY} {list.length - PER_DAY === 1 ? "altro" : "altri"}: vedi tutti
                    </button>
                  )}
                </div>
              </div>
            );
          })}
        </div>
      </div>

      {/* Sul telefono: i giorni del mese che hanno qualcosa. */}
      <div className="space-y-4 md:hidden">
        {visibleDays.length === 0 ? (
          <p className="rounded-lg border border-dashed border-line-strong px-4 py-8 text-center text-[0.8125rem] text-muted">Nessun post in questo mese.</p>
        ) : visibleDays.map((day) => (
          <section key={dayKey(day)}>
            <h3 className="mb-1.5 text-xs font-medium text-muted first-letter:uppercase">{dayFormat.format(day)}</h3>
            <div className="space-y-1.5">{byDay.get(dayKey(day))!.map((e) => e.kind === "post" ? (
              <PostChip key={e.post.id} post={e.post} onOpen={() => onChip(e.post)} large selecting={selecting} selected={selectedIds.includes(e.post.id)} disabled={selecting && !selectable(e.post)} />
            ) : (
              <NextChip key={`${e.recurring.id}-${e.at.getTime()}`} entry={e} accounts={accounts.data!} large disabled={selecting} onOpen={() => setEditingRecurring({ recurring: e.recurring })} />
            ))}</div>
          </section>
        ))}
      </div>

      {openDay && (
        <Modal open onOpenChange={(o) => !o && setOpenDay(null)} title={<span className="first-letter:uppercase">{dayFormat.format(openDay)}</span>}
          description={`${(byDay.get(dayKey(openDay)) ?? []).length} post, in ordine di ora.`}
          footer={
            <>
              <Button onClick={() => setOpenDay(null)}>Chiudi</Button>
              {admin && <Button variant="primary" icon={<Plus className="size-3.5" />}
                onClick={() => { const day = openDay; setOpenDay(null); open(undefined, newPostAt(day)); }}>Nuovo post in questo giorno</Button>}
            </>
          }>
          <div className="max-h-[60vh] space-y-1.5 overflow-y-auto">
            {(byDay.get(dayKey(openDay)) ?? []).map((e) => e.kind === "post" ? (
              <PostChip key={e.post.id} post={e.post} large onOpen={() => { setOpenDay(null); open(e.post); }} />
            ) : (
              <NextChip key={`${e.recurring.id}-${e.at.getTime()}`} entry={e} accounts={accounts.data!} large
                onOpen={() => { setOpenDay(null); setEditingRecurring({ recurring: e.recurring }); }} />
            ))}
          </div>
        </Modal>
      )}

      {editing && (
        <PostEditor post={editing.post} initialDate={editing.date} defaultProjectId={projectId || undefined} accounts={accounts.data!} projects={projects.data ?? []} admin={admin} onClose={() => setEditing(null)} />
      )}
      {editingRecurring && (
        <RecurringEditor key={editingRecurring.recurring?.id ?? "copia"} recurring={editingRecurring.recurring} template={editingRecurring.template} defaultProjectId={projectId || undefined}
          accounts={accounts.data!} projects={projects.data ?? []} admin={admin} onClose={() => setEditingRecurring(null)}
          onDuplicate={(copy) => setEditingRecurring({ template: copy })} />
      )}
    </>
  );
}

function PostChip({ post, onOpen, large, selecting, selected, disabled }: {
  post: SocialPost;
  onOpen: () => void;
  large?: boolean;
  selecting?: boolean;
  selected?: boolean;
  disabled?: boolean;
}) {
  const networks = [...new Set(post.targets.map((t) => t.network))];
  const status = post.imported ? { label: "Pubblicato altrove", tone: "ok" as const } : postStatus[post.status];
  return (
    <button type="button" title={disabled ? "Già uscito: non si cambia" : `${status.label}: ${post.text}`} aria-pressed={selecting ? selected : undefined}
      onClick={(e) => { e.stopPropagation(); if (!disabled) onOpen(); }}
      className={clsx(
        "block w-full rounded border-l-2 text-left transition-opacity",
        disabled ? "cursor-not-allowed opacity-40" : "hover:opacity-80",
        selected && "ring-2 ring-brand",
        // I post importati: stesso colore dei pubblicati, ma tratteggiati, perché non sono passati da qui.
        post.imported ? "border border-l-2 border-dashed border-ok/40 border-l-ok bg-transparent text-fg" : chipTone[post.status],
        large ? "px-3 py-2" : "px-1.5 py-1",
      )}>
      <span className="flex items-center gap-1 overflow-hidden">
        {selecting && !disabled && <input type="checkbox" readOnly tabIndex={-1} checked={!!selected} className="size-3 accent-brand" aria-hidden />}
        <span className="font-mono text-[0.6875rem] text-muted">{timeFormat.format(new Date(post.scheduledAtUtc))}</span>
        {networks.map((n) => <NetworkGlyph key={n} network={n} className="size-3 text-muted" />)}
        {post.recurringPostId && <Repeat className="size-3 text-muted" aria-label="Uscita di un post ricorrente" />}
        {large && <Badge tone={status.tone} className="ml-auto">{status.label}</Badge>}
      </span>
      <span className={clsx("block truncate", large ? "mt-1 text-[0.8125rem]" : "text-xs")}>
        {post.text || (post.media.length > 0 ? `${post.media.length} immagini` : "Senza testo")}
      </span>
    </button>
  );
}

/**
 * Un'uscita futura di un post ricorrente: non è ancora un post (lo diventa
 * all'ora giusta), quindi è tratteggiata e apre il post ricorrente.
 */
function NextChip({ entry, accounts, large, disabled, onOpen }: {
  entry: Extract<Entry, { kind: "next" }>;
  accounts: SocialAccount[];
  large?: boolean;
  disabled?: boolean;
  onOpen: () => void;
}) {
  const r = entry.recurring;
  const networks = [...new Set(accounts.filter((a) => r.accountIds.includes(a.id)).map((a) => a.network))];
  return (
    <button type="button" title={`Post ricorrente: ${r.text}`} disabled={disabled}
      onClick={(e) => { e.stopPropagation(); if (!disabled) onOpen(); }}
      className={clsx(
        "block w-full rounded border border-l-2 border-dashed border-brand/40 border-l-brand bg-transparent text-left text-fg transition-opacity",
        disabled ? "cursor-not-allowed opacity-40" : "hover:opacity-80",
        large ? "px-3 py-2" : "px-1.5 py-1",
      )}>
      <span className="flex items-center gap-1 overflow-hidden">
        <span className="font-mono text-[0.6875rem] text-muted">{timeFormat.format(entry.at)}</span>
        {networks.map((n) => <NetworkGlyph key={n} network={n} className="size-3 text-muted" />)}
        <Repeat className="size-3 text-brand-fg" aria-label="Post ricorrente" />
        {large && <Badge tone="brand" className="ml-auto">Ricorrente</Badge>}
      </span>
      <span className={clsx("block truncate", large ? "mt-1 text-[0.8125rem]" : "text-xs")}>
        {r.text || (r.media.length > 0 ? `${r.media.length} immagini` : "Senza testo")}
      </span>
    </button>
  );
}
