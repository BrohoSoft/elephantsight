import clsx from "clsx";
import { ArrowRight, Check, FolderKanban } from "lucide-react";
import type { ReactNode } from "react";
import { Link } from "react-router";
import { useCredentials, useProjects } from "../api/hooks";
import { AppIcon } from "../components/AppIcon";
import { Dashboard } from "../components/Dashboard";
import { formatInt } from "../components/format";
import { StoreGlyph } from "../components/StoreIcons";
import { EmptyState, PageHeader, PageLoader, Panel } from "../components/ui";
import { useOrg } from "../components/org";

/**
 * La prima pagina di un'organizzazione. Finché la sincronizzazione non c'è
 * (fase 2) gli indicatori sono segnaposto dichiarati come tali: meglio un
 * trattino onesto che numeri finti.
 */
export function OverviewPage() {
  const org = useOrg();
  const projects = useProjects(org.id);
  const credentials = useCredentials(org.id);

  if (projects.isPending || credentials.isPending) return <PageLoader />;

  const hasCredentials = (credentials.data?.length ?? 0) > 0;
  const hasProjects = (projects.data?.length ?? 0) > 0;
  const hasApps = projects.data?.some((p) => p.apps.length > 0) ?? false;
  const setupDone = hasCredentials && hasProjects && hasApps;

  return (
    <>
      <PageHeader title={org.name} description="Tutte le app dell'organizzazione, App Store e Google Play insieme." />

      {!setupDone && (
        <Panel title="Per cominciare" description="Tre passi, poi i dati arrivano da soli." className="mb-6">
          <ol className="divide-y divide-line">
            <SetupStep done={hasCredentials} n={1} to={`/o/${org.id}/credentials`} title="Carica una chiave degli store">
              La chiave .p8 di App Store Connect o il JSON di un service account Google Play.
            </SetupStep>
            <SetupStep done={hasProjects} n={2} to={`/o/${org.id}/projects`} title="Crea un progetto">
              Un progetto è un'app vista come una cosa sola, anche se è su tutti e due gli store.
            </SetupStep>
            <SetupStep done={hasApps} n={3} to={hasProjects ? `/o/${org.id}/projects/${projects.data![0].id}` : `/o/${org.id}/projects`} title="Collega le app al progetto">
              Scegli l'app dall'elenco che la chiave vede.
            </SetupStep>
          </ol>
        </Panel>
      )}

      <Dashboard orgId={org.id}>
        {(m) => (
          <Panel
            title="Progetti"
            actions={
              <Link to={`/o/${org.id}/projects`} className="inline-flex items-center gap-1 text-xs text-muted hover:text-fg">
                Tutti <ArrowRight className="size-3" />
              </Link>
            }
          >
            {hasProjects ? (
              <table className="w-full text-[13px]">
                <thead className="text-xs text-muted">
                  <tr className="border-b border-line">
                    <th className="px-4 py-2 text-left font-medium">Progetto</th>
                    <th className="px-4 py-2 text-right font-medium">Download</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-line">
                  {projects.data!.map((p) => {
                    const totals = m.byProject.find((t) => t.projectId === p.id);
                    return (
                      <tr key={p.id} className="hover:bg-hover/50">
                        <td className="px-4 py-2.5">
                          <Link to={`/o/${org.id}/projects/${p.id}`} className="flex items-center gap-2.5">
                            <AppIcon src={p.iconUrl} name={p.name} size="sm" />
                            <span className="truncate text-fg">{p.name}</span>
                            <span className="flex gap-1">
                              {p.apps.map((a) => <StoreGlyph key={a.id} store={a.store} className={clsx("size-3", a.store === "AppStore" ? "text-ios" : "text-android")} />)}
                            </span>
                          </Link>
                        </td>
                        <td className="px-4 py-2.5 text-right tabular-nums">{totals ? formatInt(totals.downloads) : "—"}</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            ) : (
              <EmptyState icon={<FolderKanban className="size-5" />} title="Nessun progetto">
                Crea il primo progetto e collega le sue app.
              </EmptyState>
            )}
          </Panel>
        )}
      </Dashboard>
    </>
  );
}

function SetupStep({ done, n, to, title, children }: { done: boolean; n: number; to: string; title: string; children: ReactNode }) {
  return (
    <li>
      <Link to={to} className="flex items-center gap-4 px-4 py-3.5 hover:bg-hover/50">
        <span
          className={clsx(
            "flex size-6 shrink-0 items-center justify-center rounded-full border text-xs",
            done ? "border-ok/40 bg-ok/10 text-ok" : "border-line-strong text-muted",
          )}
        >
          {done ? <Check className="size-3.5" /> : n}
        </span>
        <span className="min-w-0 flex-1">
          <span className={clsx("block text-[13px]", done ? "text-muted line-through decoration-faint" : "text-fg")}>{title}</span>
          <span className="block text-xs text-faint">{children}</span>
        </span>
        {!done && <ArrowRight className="size-4 text-faint" />}
      </Link>
    </li>
  );
}
