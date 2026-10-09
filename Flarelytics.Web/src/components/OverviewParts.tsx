import clsx from "clsx";
import { ArrowRight, Repeat } from "lucide-react";
import type { ReactNode } from "react";
import { Link } from "react-router";
import { useReleases } from "../api/hooks";
import type { LogItem, Project, Store, StoreReleases, UpcomingItem } from "../api/types";
import { AppIcon } from "./AppIcon";
import { formatDateTime } from "./org";
import { NetworkGlyph } from "./SocialIcons";
import { StageBadge } from "./StageBadge";
import { StoreGlyph } from "./StoreIcons";
import { Badge, Panel, Spinner } from "./ui";

/*
 * I pezzi delle panoramiche (organizzazione e progetto): numeri, prossimi
 * post, versioni delle app, ultimi log.
 */

/** Un numero con la sua etichetta, cliccabile verso la pagina che lo spiega. */
export function StatTile({ label, value, to, tone, hint }: { label: string; value: ReactNode; to?: string; tone?: "bad" | "warn"; hint?: string }) {
  const body = (
    <>
      <p className="text-xs text-muted">{label}</p>
      <p className={clsx("mt-1 text-2xl font-medium tabular-nums", tone === "bad" ? "text-bad" : tone === "warn" ? "text-warn" : "text-fg")}>{value}</p>
      {hint && <p className="mt-0.5 text-[0.6875rem] text-faint">{hint}</p>}
    </>
  );
  const box = "block rounded-lg border border-line bg-panel px-4 py-3";
  return to ? <Link to={to} className={clsx(box, "hover:bg-hover/40")}>{body}</Link> : <div className={box}>{body}</div>;
}

/** Un collegamento "Tutti →" nell'intestazione di un riquadro. */
export const MoreLink = ({ to, children = "Tutti" }: { to: string; children?: ReactNode }) => (
  <Link to={to} className="inline-flex items-center gap-1 text-xs text-muted hover:text-fg">{children} <ArrowRight className="size-3" /></Link>
);

/** I prossimi post, programmati e uscite dei ricorrenti, in ordine di ora. */
export function UpcomingList({ items, projects, calendarTo }: { items: UpcomingItem[]; projects?: Project[]; calendarTo: string }) {
  if (items.length === 0) return <p className="px-4 py-6 text-center text-[0.8125rem] text-muted">Nessun post in arrivo.</p>;
  return (
    <ul className="divide-y divide-line">
      {items.map((u, i) => (
        <li key={`${u.postId ?? u.recurringPostId}-${i}`}>
          <Link to={calendarTo} className="flex items-start gap-3 px-4 py-2.5 hover:bg-hover/40">
            <span className="w-24 shrink-0 font-mono text-[0.6875rem] text-muted">{formatDateTime(u.atUtc)}</span>
            <span className="min-w-0 flex-1">
              <span className="line-clamp-2 text-[0.8125rem] text-fg">{u.text || "Senza testo"}</span>
              <span className="mt-0.5 flex flex-wrap items-center gap-1.5">
                {u.networks.map((n) => <NetworkGlyph key={n} network={n} className="size-3 text-muted" />)}
                {u.recurringPostId && <span className="inline-flex items-center gap-1 text-[0.6875rem] text-brand-fg"><Repeat className="size-3" /> ricorrente</span>}
                {projects && u.projectId && <Badge>{projects.find((p) => p.id === u.projectId)?.name ?? "Progetto"}</Badge>}
              </span>
            </span>
          </Link>
        </li>
      ))}
    </ul>
  );
}

/** La versione in uso su uno store: quella pubblicata (o in rilascio graduale), altrimenti l'ultima con il suo stato. */
function currentVersion(r: StoreReleases) {
  return r.versions.find((v) => v.stage === "Live") ?? r.versions.find((v) => v.stage === "Rolling") ?? r.versions[0] ?? null;
}

/** Le versioni delle app di un progetto, lette dagli store (con la stessa cache della pagina Versioni). */
export function ProjectVersions({ orgId, project, showName }: { orgId: string; project: Project; showName?: boolean }) {
  const releases = useReleases(orgId, project.id);
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-1.5 px-4 py-2.5">
      {showName && (
        <Link to={`/o/${orgId}/projects/${project.id}/releases`} className="flex min-w-40 flex-1 items-center gap-2.5">
          <AppIcon src={project.iconUrl} name={project.name} size="sm" />
          <span className="truncate text-[0.8125rem] text-fg">{project.name}</span>
        </Link>
      )}
      {project.apps.length === 0 ? <span className="text-xs text-faint">Nessuna app collegata</span>
        : releases.isPending ? <Spinner />
        : (["AppStore", "GooglePlay"] as Store[]).filter((s) => project.apps.some((a) => a.store === s)).map((store) => {
          const r = releases.data?.find((x) => x.store === store);
          const v = r && !r.error ? currentVersion(r) : null;
          return (
            <span key={store} className="flex items-center gap-1.5 text-xs">
              <StoreGlyph store={store} className={clsx("size-3.5", store === "AppStore" ? "text-ios" : "text-android")} />
              {r?.error ? <span className="text-bad" title={r.error}>non leggibile</span>
                : v ? <><span className="font-mono text-fg">{v.version}</span><StageBadge stage={v.stage} raw={v.rawState} /></>
                : <span className="text-faint">nessuna versione</span>}
            </span>
          );
        })}
    </div>
  );
}

const levelTone: Record<string, "neutral" | "warn" | "bad"> = { Information: "neutral", Warning: "warn", Error: "bad", Critical: "bad" };
const levelLabel: Record<string, string> = { Information: "Info", Warning: "Avviso", Error: "Errore", Critical: "Critico" };

/** Gli ultimi messaggi del log, compatti: il dettaglio sta nella pagina Log. */
export function RecentLogs({ items, logsTo }: { items: LogItem[]; logsTo: string }) {
  return (
    <Panel title="Ultimi log" actions={<MoreLink to={logsTo} />}>
      {items.length === 0 ? <p className="px-4 py-6 text-center text-[0.8125rem] text-muted">Nessun messaggio.</p> : (
        <ul className="divide-y divide-line">
          {items.map((l) => (
            <li key={l.id} className="flex items-start gap-2 px-4 py-2">
              <span className="w-24 shrink-0 font-mono text-[0.6875rem] text-faint">{formatDateTime(l.timestampUtc)}</span>
              <Badge tone={levelTone[l.level] ?? "neutral"}>{levelLabel[l.level] ?? l.level}</Badge>
              <span className="min-w-0 flex-1 truncate text-xs text-fg" title={l.message}>{l.message}</span>
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}
