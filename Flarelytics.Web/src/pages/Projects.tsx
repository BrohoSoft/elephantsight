import { FolderKanban, Plus } from "lucide-react";
import { useState } from "react";
import { Link, useNavigate } from "react-router";
import { errorMessage } from "../api/client";
import { canAdmin, keys, useApiMutation, useProjects } from "../api/hooks";
import type { Project } from "../api/types";
import { AppIcon } from "../components/AppIcon";
import { StoreBadge, StoreGlyph } from "../components/StoreIcons";
import { Alert, Button, EmptyState, Field, Input, Modal, PageHeader, PageLoader, Textarea } from "../components/ui";
import { formatDate, useOrg } from "../components/org";

export function ProjectsPage() {
  const org = useOrg();
  const projects = useProjects(org.id);
  const [creating, setCreating] = useState(false);

  if (projects.isPending) return <PageLoader />;

  const count = projects.data?.length ?? 0;

  return (
    <>
      <PageHeader
        title="Progetti"
        description={count === 1 ? "1 progetto." : `${count} progetti.`}
        actions={
          canAdmin(org.role) && (
            <Button variant="primary" icon={<Plus className="size-3.5" />} onClick={() => setCreating(true)}>
              Nuovo progetto
            </Button>
          )
        }
      />

      {count === 0 ? (
        <div className="rounded-lg border border-dashed border-line-strong">
          <EmptyState
            icon={<FolderKanban className="size-5" />}
            title="Nessun progetto"
            action={canAdmin(org.role) && <Button variant="primary" onClick={() => setCreating(true)}>Crea il primo progetto</Button>}
          >
            Un progetto riunisce la stessa app su App Store e Google Play.
          </EmptyState>
        </div>
      ) : (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {projects.data!.map((p) => (
            <ProjectCard key={p.id} orgId={org.id} project={p} />
          ))}
        </div>
      )}

      <CreateProjectModal orgId={org.id} open={creating} onOpenChange={setCreating} />
    </>
  );
}

function ProjectCard({ orgId, project }: { orgId: string; project: Project }) {
  return (
    <Link
      to={`/o/${orgId}/projects/${project.id}`}
      className="group flex flex-col rounded-lg border border-line bg-panel p-4 transition-colors hover:border-line-strong hover:bg-panel-2"
    >
      <div className="flex items-start justify-between gap-3">
        <div className="flex min-w-0 items-center gap-3">
          <AppIcon src={project.iconUrl} name={project.name} />
          <h3 className="truncate text-sm font-medium text-fg">{project.name}</h3>
        </div>
        <span className="flex gap-1 text-faint">
          {(["AppStore", "GooglePlay"] as const).map((s) => (
            <StoreGlyph
              key={s}
              store={s}
              className={project.apps.some((a) => a.store === s) ? (s === "AppStore" ? "text-ios" : "text-android") : "opacity-30"}
            />
          ))}
        </span>
      </div>
      <p className="mt-1 line-clamp-2 min-h-8 text-xs text-muted">{project.description ?? "Nessuna descrizione"}</p>
      <div className="mt-4 flex flex-wrap items-center gap-1.5">
        {project.apps.length === 0 ? (
          <span className="text-xs text-warn">Nessuna app collegata</span>
        ) : (
          project.apps.map((a) => <StoreBadge key={a.id} store={a.store} />)
        )}
        <span className="ml-auto text-[11px] text-faint">{formatDate(project.createdAtUtc)}</span>
      </div>
    </Link>
  );
}

function CreateProjectModal({ orgId, open, onOpenChange }: { orgId: string; open: boolean; onOpenChange: (v: boolean) => void }) {
  const navigate = useNavigate();
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const create = useApiMutation<void, Project>(
    () => ({ path: `/orgs/${orgId}/projects`, body: { name, description: description || null } }),
    [keys.projects(orgId)],
  );

  return (
    <Modal
      open={open}
      onOpenChange={onOpenChange}
      title="Nuovo progetto"
      description="Dopo averlo creato colleghi le sue app sugli store."
      footer={
        <>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>Annulla</Button>
          <Button
            variant="primary"
            loading={create.isPending}
            disabled={!name.trim()}
            onClick={() =>
              create.mutate(undefined, {
                onSuccess: (p) => {
                  onOpenChange(false);
                  setName("");
                  setDescription("");
                  navigate(`/o/${orgId}/projects/${p.id}`);
                },
              })
            }
          >
            Crea progetto
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <Field label="Nome">
          <Input autoFocus value={name} onChange={(e) => setName(e.target.value)} placeholder="App Meteo" />
        </Field>
        <Field label="Descrizione (facoltativa)">
          <Textarea rows={3} className="font-sans text-[13px]" value={description} onChange={(e) => setDescription(e.target.value)} />
        </Field>
        {create.error && <Alert tone="bad">{errorMessage(create.error)}</Alert>}
      </div>
    </Modal>
  );
}
