import clsx from "clsx";
import { ArrowRight, FolderKanban } from "lucide-react";
import { Link } from "react-router";
import { useProjects } from "../api/hooks";
import { AppIcon } from "../components/AppIcon";
import { Dashboard } from "../components/Dashboard";
import { formatInt } from "../components/format";
import { StoreGlyph } from "../components/StoreIcons";
import { EmptyState, PageHeader, PageLoader, Panel } from "../components/ui";
import { useOrg } from "../components/org";

/**
 * I download di tutti i progetti che il membro vede, App Store e Google Play
 * insieme, con il dettaglio per progetto. La panoramica dell'organizzazione è
 * un'altra pagina (OverviewPage); quella di un singolo progetto sta nel progetto.
 */
export function AnalyticsPage() {
  const org = useOrg();
  const projects = useProjects(org.id);

  if (projects.isPending) return <PageLoader />;
  const hasProjects = (projects.data?.length ?? 0) > 0;

  return (
    <>
      <PageHeader title="Download" description="I download di tutte le app, App Store e Google Play insieme, con il dettaglio per progetto." />

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
              <table className="w-full text-[0.8125rem]">
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
