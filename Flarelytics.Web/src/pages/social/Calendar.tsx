import clsx from "clsx";
import { CalendarDays, ChevronLeft, ChevronRight, Plus } from "lucide-react";
import { useMemo, useState } from "react";
import { Link } from "react-router";
import { canAdmin, useProjects, useSocialAccounts, useSocialPosts } from "../../api/hooks";
import type { SocialPost } from "../../api/types";
import { useOrg } from "../../components/org";
import { NetworkGlyph } from "../../components/SocialIcons";
import { Badge, Button, EmptyState, PageHeader, PageLoader, Select } from "../../components/ui";
import { PostEditor, postStatus } from "../../social/PostEditor";

const monthFormat = new Intl.DateTimeFormat("it-IT", { month: "long", year: "numeric" });
const timeFormat = new Intl.DateTimeFormat("it-IT", { hour: "2-digit", minute: "2-digit" });
const dayFormat = new Intl.DateTimeFormat("it-IT", { weekday: "long", day: "numeric", month: "long" });
const WEEKDAYS = ["lun", "mar", "mer", "gio", "ven", "sab", "dom"];

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
};

/**
 * Il calendario dei post: il mese, con i post di ogni giorno. Un clic su un
 * giorno vuoto apre un post nuovo per quel giorno, un clic su un post lo apre.
 * Sul telefono la griglia diventa un elenco dei giorni con qualcosa.
 */
export function SocialCalendarPage() {
  const org = useOrg();
  const admin = canAdmin(org.role);
  const [month, setMonth] = useState(() => new Date(new Date().getFullYear(), new Date().getMonth(), 1));
  const [projectId, setProjectId] = useState("");
  const [editing, setEditing] = useState<{ post?: SocialPost; date?: Date } | null>(null);

  const days = useMemo(() => monthGrid(month), [month]);
  const from = days[0];
  const to = new Date(days[41].getFullYear(), days[41].getMonth(), days[41].getDate() + 1);
  const posts = useSocialPosts(org.id, from, to, projectId || undefined);
  const accounts = useSocialAccounts(org.id);
  const projects = useProjects(org.id);

  const byDay = useMemo(() => {
    const map = new Map<string, SocialPost[]>();
    for (const p of posts.data ?? []) {
      const key = dayKey(new Date(p.scheduledAtUtc));
      map.set(key, [...(map.get(key) ?? []), p]);
    }
    return map;
  }, [posts.data]);

  if (accounts.isPending || projects.isPending) return <PageLoader />;

  const today = new Date();
  const shift = (delta: number) => setMonth((m) => new Date(m.getFullYear(), m.getMonth() + delta, 1));
  /** Un giorno nuovo parte alle 10, oggi fra un'ora: mai nel passato. */
  const newPostAt = (day: Date) => {
    const at = new Date(day.getFullYear(), day.getMonth(), day.getDate(), 10, 0);
    return at.getTime() < Date.now() ? new Date(Math.ceil((Date.now() + 3_600_000) / 900_000) * 900_000) : at;
  };
  const open = (post?: SocialPost, date?: Date) => setEditing({ post, date });

  const visibleDays = days.filter((d) => d.getMonth() === month.getMonth() && byDay.has(dayKey(d)));

  return (
    <>
      <PageHeader
        title="Calendario social"
        description="I post programmati su tutti gli account dell'organizzazione. All'ora indicata li pubblica WatchStore."
        actions={
          <>
            {(projects.data?.length ?? 0) > 0 && (
              <Select value={projectId} onChange={(e) => setProjectId(e.target.value)} className="w-44" aria-label="Progetto">
                <option value="">Tutti i progetti</option>
                {projects.data!.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
              </Select>
            )}
            {admin && <Button variant="primary" icon={<Plus className="size-3.5" />} onClick={() => open(undefined, newPostAt(today))}>Nuovo post</Button>}
          </>
        }
      />

      {accounts.data!.length === 0 && (
        <div className="mb-6 rounded-lg border border-dashed border-line-strong">
          <EmptyState icon={<CalendarDays className="size-5" />} title="Nessun account social collegato"
            action={<Link to={`/o/${org.id}/social/accounts`}><Button variant="primary">Collega un account</Button></Link>}>
            Bluesky, Mastodon, Instagram e Pagine Facebook. Puoi già scrivere bozze, ma per pubblicare serve almeno un account.
          </EmptyState>
        </div>
      )}

      <div className="mb-3 flex items-center gap-2">
        <Button size="sm" variant="ghost" icon={<ChevronLeft className="size-4" />} aria-label="Mese prima" onClick={() => shift(-1)} />
        <Button size="sm" variant="ghost" icon={<ChevronRight className="size-4" />} aria-label="Mese dopo" onClick={() => shift(1)} />
        <h2 className="text-sm font-medium text-fg first-letter:uppercase">{monthFormat.format(month)}</h2>
        <Button size="sm" className="ml-1" onClick={() => setMonth(new Date(today.getFullYear(), today.getMonth(), 1))}>Oggi</Button>
        {posts.isFetching && <span className="text-xs text-faint">aggiornamento…</span>}
      </div>

      {/* Dal tablet in su: la griglia del mese. */}
      <div className="hidden overflow-hidden rounded-lg border border-line bg-panel md:block">
        <div className="grid grid-cols-7 border-b border-line bg-panel-2/50">
          {WEEKDAYS.map((d) => <div key={d} className="px-2 py-1.5 text-[11px] font-medium tracking-wide text-faint uppercase">{d}</div>)}
        </div>
        <div className="grid grid-cols-7">
          {days.map((day, i) => {
            const inMonth = day.getMonth() === month.getMonth();
            const list = byDay.get(dayKey(day)) ?? [];
            return (
              <div key={i}
                onClick={() => admin && open(undefined, newPostAt(day))}
                className={clsx(
                  "group min-h-28 border-line p-1.5 [&:not(:nth-child(7n))]:border-r [&:nth-child(n+8)]:border-t",
                  !inMonth && "bg-panel-2/40",
                  admin && "cursor-pointer hover:bg-hover/40",
                )}>
                <div className="mb-1 flex items-center justify-between">
                  <span className={clsx(
                    "flex size-6 items-center justify-center rounded-full text-xs",
                    sameDay(day, today) ? "bg-brand font-medium text-brand-ink" : inMonth ? "text-muted" : "text-faint",
                  )}>{day.getDate()}</span>
                  {admin && <Plus className="size-3.5 text-faint opacity-0 group-hover:opacity-100" />}
                </div>
                <div className="space-y-1">
                  {list.slice(0, 3).map((p) => <PostChip key={p.id} post={p} onOpen={() => open(p)} />)}
                  {list.length > 3 && <p className="px-1 text-[11px] text-faint">+{list.length - 3} altri</p>}
                </div>
              </div>
            );
          })}
        </div>
      </div>

      {/* Sul telefono: i giorni del mese che hanno qualcosa. */}
      <div className="space-y-4 md:hidden">
        {visibleDays.length === 0 ? (
          <p className="rounded-lg border border-dashed border-line-strong px-4 py-8 text-center text-[13px] text-muted">Nessun post in questo mese.</p>
        ) : visibleDays.map((day) => (
          <section key={dayKey(day)}>
            <h3 className="mb-1.5 text-xs font-medium text-muted first-letter:uppercase">{dayFormat.format(day)}</h3>
            <div className="space-y-1.5">{byDay.get(dayKey(day))!.map((p) => <PostChip key={p.id} post={p} onOpen={() => open(p)} large />)}</div>
          </section>
        ))}
      </div>

      {editing && (
        <PostEditor post={editing.post} initialDate={editing.date} accounts={accounts.data!} projects={projects.data ?? []} admin={admin} onClose={() => setEditing(null)} />
      )}
    </>
  );
}

function PostChip({ post, onOpen, large }: { post: SocialPost; onOpen: () => void; large?: boolean }) {
  const networks = [...new Set(post.targets.map((t) => t.network))];
  const status = postStatus[post.status];
  return (
    <button type="button" title={`${status.label}: ${post.text}`}
      onClick={(e) => { e.stopPropagation(); onOpen(); }}
      className={clsx("block w-full rounded border-l-2 text-left transition-opacity hover:opacity-80", chipTone[post.status], large ? "px-3 py-2" : "px-1.5 py-1")}>
      <span className="flex items-center gap-1">
        <span className="font-mono text-[11px] text-muted">{timeFormat.format(new Date(post.scheduledAtUtc))}</span>
        {networks.map((n) => <NetworkGlyph key={n} network={n} className="size-3 text-muted" />)}
        {large && <Badge tone={status.tone} className="ml-auto">{status.label}</Badge>}
      </span>
      <span className={clsx("block truncate", large ? "mt-1 text-[13px]" : "text-xs")}>
        {post.text || (post.media.length > 0 ? `${post.media.length} immagini` : "Senza testo")}
      </span>
    </button>
  );
}
