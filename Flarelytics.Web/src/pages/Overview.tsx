import clsx from "clsx";
import { ArrowRight, Check, FolderKanban } from "lucide-react";
import type { ReactNode } from "react";
import { Link } from "react-router";
import { useCredentials, useOverview, useProjects } from "../api/hooks";
import type { Project } from "../api/types";
import { AppIcon } from "../components/AppIcon";
import { canManageOrg, hasSocial, hasStore, useOrg } from "../components/org";
import { MoreLink, ProjectVersions, RecentLogs, StatTile, UpcomingList } from "../components/OverviewParts";
import { StoreDots } from "../components/ProjectNav";
import { recentProjects } from "../components/recentProjects";
import { EmptyState, PageHeader, PageLoader, Panel } from "../components/ui";

/**
 * La prima pagina dell'organizzazione: quello che si vuole vedere appena si
 * entra. Numeri e prossimi post dei social, gli ultimi progetti aperti, la
 * versione delle app sugli store, gli ultimi log. Ogni parte compare solo a
 * chi la può vedere; i download stanno nella pagina Download e nei progetti.
 */
export function OverviewPage() {
  const org = useOrg();
  const overview = useOverview(org.id);
  const projects = useProjects(org.id);
  const manage = canManageOrg(org);
  const credentials = useCredentials(org.id, manage && hasStore(org));

  if (overview.isPending || projects.isPending) return <PageLoader />;

  const o = overview.data;
  const all = projects.data ?? [];
  // Gli ultimi aperti in questo browser, poi gli altri: la panoramica mostra sempre qualcosa.
  const recentIds = recentProjects(org.id);
  const ordered = [...recentIds.map((id) => all.find((p) => p.id === id)).filter((p): p is Project => !!p), ...all.filter((p) => !recentIds.includes(p.id))];
  const base = `/o/${org.id}`;

  const setup = manage && hasStore(org) && credentials.data
    ? { credentials: credentials.data.length > 0, projects: all.length > 0, apps: all.some((p) => p.apps.length > 0) }
    : null;

  return (
    <>
      <PageHeader title={org.name} description="Quello che succede adesso: post in arrivo, progetti, versioni delle app, ultimi messaggi." />

      {setup && !(setup.credentials && setup.projects && setup.apps) && (
        <Panel title="Per cominciare" description="Tre passi, poi i dati degli store arrivano da soli." className="mb-6">
          <ol className="divide-y divide-line">
            <SetupStep done={setup.credentials} n={1} to={`${base}/credentials`} title="Carica una chiave degli store">
              La chiave .p8 di App Store Connect o il JSON di un service account Google Play.
            </SetupStep>
            <SetupStep done={setup.projects} n={2} to={`${base}/projects`} title="Crea un progetto">
              Un progetto è un'app vista come una cosa sola, anche se è su tutti e due gli store.
            </SetupStep>
            <SetupStep done={setup.apps} n={3} to={all.length > 0 ? `${base}/projects/${all[0].id}/settings` : `${base}/projects`} title="Collega le app al progetto">
              Scegli l'app dall'elenco che la chiave vede.
            </SetupStep>
          </ol>
        </Panel>
      )}

      <div className="mb-6 grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-5">
        <StatTile label="Progetti" value={o?.projects ?? all.length} to={`${base}/projects`} />
        {o?.social && (
          <>
            <StatTile label="Post programmati" value={o.social.scheduled} to={`${base}/social`} />
            <StatTile label="Da programmare" value={o.social.inbox} to={`${base}/social/inbox`} tone={o.social.inbox > 0 ? "warn" : undefined} />
            <StatTile label="Post ricorrenti attivi" value={o.social.recurringActive} to={`${base}/social/recurring`} />
            <StatTile label="Non usciti (7 giorni)" value={o.social.failedLastWeek} to={manage ? `${base}/logs?level=Warning` : `${base}/social`}
              tone={o.social.failedLastWeek > 0 ? "bad" : undefined} hint={o.social.failedLastWeek > 0 ? "Apri per vedere il motivo" : undefined} />
          </>
        )}
      </div>

      <div className="grid gap-6 lg:grid-cols-2">
        {o?.social && hasSocial(org) && (
          <Panel title="Prossimi post" actions={<MoreLink to={`${base}/social`}>Calendario</MoreLink>}>
            <UpcomingList items={o.social.upcoming} projects={all} calendarTo={`${base}/social`} />
          </Panel>
        )}

        <Panel title={recentIds.length > 0 ? "Ultimi progetti aperti" : "Progetti"} actions={<MoreLink to={`${base}/projects`} />}>
          {ordered.length === 0 ? (
            <EmptyState icon={<FolderKanban className="size-5" />} title="Nessun progetto">
              {manage ? "Crea il primo progetto dalla pagina Progetti." : "Non vedi ancora nessun progetto."}
            </EmptyState>
          ) : (
            <ul className="divide-y divide-line">
              {ordered.slice(0, 6).map((p) => (
                <li key={p.id}>
                  <Link to={`${base}/projects/${p.id}`} className="flex items-center gap-3 px-4 py-2.5 hover:bg-hover/40">
                    <AppIcon src={p.iconUrl} name={p.name} size="sm" />
                    <span className="min-w-0 flex-1 truncate text-[0.8125rem] text-fg">{p.name}</span>
                    {hasStore(org) && <StoreDots project={p} />}
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </Panel>

        {hasStore(org) && ordered.some((p) => p.apps.length > 0) && (
          <Panel title="Versioni delle app" description="Quella pubblicata su ogni store, letta dallo store.">
            <div className="divide-y divide-line">
              {ordered.filter((p) => p.apps.length > 0).slice(0, 6).map((p) => <ProjectVersions key={p.id} orgId={org.id} project={p} showName />)}
            </div>
          </Panel>
        )}

        {o?.logs && <RecentLogs items={o.logs} logsTo={`${base}/logs`} />}
      </div>
    </>
  );
}

function SetupStep({ done, n, to, title, children }: { done: boolean; n: number; to: string; title: string; children: ReactNode }) {
  return (
    <li>
      <Link to={to} className="flex items-center gap-4 px-4 py-3.5 hover:bg-hover/50">
        <span className={clsx("flex size-6 shrink-0 items-center justify-center rounded-full border text-xs", done ? "border-ok/40 bg-ok/10 text-ok" : "border-line-strong text-muted")}>
          {done ? <Check className="size-3.5" /> : n}
        </span>
        <span className="min-w-0 flex-1">
          <span className={clsx("block text-[0.8125rem]", done ? "text-muted line-through decoration-faint" : "text-fg")}>{title}</span>
          <span className="block text-xs text-faint">{children}</span>
        </span>
        {!done && <ArrowRight className="size-4 text-faint" />}
      </Link>
    </li>
  );
}
