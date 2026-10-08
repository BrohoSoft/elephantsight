import { ArrowLeft, Link2, Search, Trash2, Unlink } from "lucide-react";
import { useMemo, useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { errorMessage } from "../api/client";
import { canAdmin, keys, useApiMutation, useCredentialApps, useCredentials, useProject } from "../api/hooks";
import type { Project, ProjectApp, Store } from "../api/types";
import { AppIcon } from "../components/AppIcon";
import { StoreBadge, StoreGlyph, storeName } from "../components/StoreIcons";
import { Alert, Button, EmptyState, Field, Input, Modal, PageLoader, Select, Spinner, Textarea } from "../components/ui";
import { useOrg } from "../components/org";
import { Dashboard } from "../components/Dashboard";

export function ProjectDetailPage() {
  const org = useOrg();
  const { projectId = "" } = useParams();
  const project = useProject(org.id, projectId);
  const [linking, setLinking] = useState<Store | null>(null);
  const [editing, setEditing] = useState(false);

  if (project.isPending) return <PageLoader />;
  if (project.error) return <Alert tone="bad">{errorMessage(project.error)}</Alert>;

  const p = project.data;
  const admin = canAdmin(org.role);

  return (
    <>
      <Link to={`/o/${org.id}/projects`} className="mb-4 inline-flex items-center gap-1 text-xs text-muted hover:text-fg">
        <ArrowLeft className="size-3" /> Progetti
      </Link>

      <div className="mb-6 flex flex-wrap items-end justify-between gap-4">
        <div className="flex min-w-0 items-center gap-4">
          <AppIcon src={p.iconUrl} name={p.name} size="lg" />
          <div className="min-w-0">
            <h1 className="text-xl font-medium tracking-tight text-fg">{p.name}</h1>
            {p.description && <p className="mt-1 text-[13px] text-muted">{p.description}</p>}
            <div className="mt-2 flex gap-1.5">
              {p.apps.map((a) => <StoreBadge key={a.id} store={a.store} />)}
            </div>
          </div>
        </div>
        {admin && <Button onClick={() => setEditing(true)}>Modifica</Button>}
      </div>

      <div className="mb-6 grid gap-3 md:grid-cols-2">
        {(["AppStore", "GooglePlay"] as Store[]).map((store) => (
          <StoreSlot key={store} orgId={org.id} project={p} store={store} admin={admin} onLink={() => setLinking(store)} />
        ))}
      </div>

      <Dashboard orgId={org.id} projectId={p.id} />

      {linking && <LinkAppModal orgId={org.id} project={p} store={linking} onClose={() => setLinking(null)} />}
      <EditProjectModal orgId={org.id} project={p} open={editing} onOpenChange={setEditing} />
    </>
  );
}

function StoreSlot({ orgId, project, store, admin, onLink }: { orgId: string; project: Project; store: Store; admin: boolean; onLink: () => void }) {
  const app: ProjectApp | undefined = project.apps.find((a) => a.store === store);
  const unlink = useApiMutation(
    () => ({ path: `/orgs/${orgId}/projects/${project.id}/apps/${store}`, method: "DELETE" }),
    [keys.project(orgId, project.id), keys.projects(orgId)],
  );
  const tint = store === "AppStore" ? "text-ios" : "text-android";

  return (
    <div className="rounded-lg border border-line bg-panel p-4">
      <div className="flex items-center gap-2">
        <StoreGlyph store={store} className={tint} />
        <span className="text-[13px] font-medium text-fg">{storeName(store)}</span>
      </div>

      {app ? (
        <div className="mt-3 flex items-end justify-between gap-3">
          <div className="flex min-w-0 items-center gap-3">
            <AppIcon src={app.iconUrl} name={app.displayName ?? app.externalAppId} />
            <div className="min-w-0">
            <p className="truncate text-sm text-fg">{app.displayName ?? app.externalAppId}</p>
            <p className="mt-0.5 truncate font-mono text-xs text-faint">{app.externalAppId}</p>
            <p className="mt-1 text-xs text-muted">Con la chiave «{app.credentialLabel}»</p>
            </div>
          </div>
          {admin && (
            <div className="flex shrink-0 gap-1.5">
              <Button size="sm" variant="ghost" onClick={onLink}>Cambia</Button>
              <Button size="sm" variant="ghost" icon={<Unlink className="size-3" />} loading={unlink.isPending} onClick={() => unlink.mutate()}>
                Scollega
              </Button>
            </div>
          )}
        </div>
      ) : (
        <div className="mt-3 flex items-center justify-between gap-3">
          <p className="text-xs text-muted">Nessuna app collegata.</p>
          {admin && (
            <Button size="sm" variant="primary" icon={<Link2 className="size-3" />} onClick={onLink}>
              Collega
            </Button>
          )}
        </div>
      )}
    </div>
  );
}

/**
 * Collegare un'app: si sceglie la chiave, e dalla chiave l'elenco delle app
 * che vede. Se l'elenco non arriva (permessi Google non ancora propagati) si
 * può scrivere l'id a mano.
 */
function LinkAppModal({ orgId, project, store, onClose }: { orgId: string; project: Project; store: Store; onClose: () => void }) {
  const credentials = useCredentials(orgId);
  const options = (credentials.data ?? []).filter((c) => c.store === store);
  const [credentialId, setCredentialId] = useState<string | null>(null);
  const selected = credentialId ?? options[0]?.id ?? null;
  const apps = useCredentialApps(orgId, selected);

  const [filter, setFilter] = useState("");
  const [manual, setManual] = useState(false);
  const [externalAppId, setExternalAppId] = useState("");
  const [displayName, setDisplayName] = useState("");

  const link = useApiMutation<{ externalAppId: string; displayName: string | null }>(
    (body) => ({ path: `/orgs/${orgId}/projects/${project.id}/apps`, method: "PUT", body: { credentialId: selected, ...body } }),
    [keys.project(orgId, project.id), keys.projects(orgId)],
  );

  const visible = useMemo(
    () => (apps.data ?? []).filter((a) => `${a.name} ${a.bundleId} ${a.externalId}`.toLowerCase().includes(filter.toLowerCase())),
    [apps.data, filter],
  );

  const submit = (id: string, name: string | null) => link.mutate({ externalAppId: id, displayName: name }, { onSuccess: onClose });

  return (
    <Modal open onOpenChange={(o) => !o && onClose()} title={`Collega l'app ${storeName(store)}`} wide>
      {credentials.isPending ? (
        <Spinner />
      ) : options.length === 0 ? (
        <EmptyState title={`Nessuna chiave ${storeName(store)}`}>
          Prima carica una chiave in <Link to={`/o/${orgId}/credentials`} className="text-fg underline underline-offset-4">Chiavi degli store</Link>.
        </EmptyState>
      ) : (
        <div className="space-y-4">
          {options.length > 1 && (
            <Field label="Chiave">
              <Select value={selected ?? ""} onChange={(e) => setCredentialId(e.target.value)}>
                {options.map((c) => <option key={c.id} value={c.id}>{c.label}</option>)}
              </Select>
            </Field>
          )}

          {manual ? (
            <ManualAppForm store={store} externalAppId={externalAppId} setExternalAppId={setExternalAppId} displayName={displayName} setDisplayName={setDisplayName} />
          ) : apps.isPending ? (
            <div className="flex items-center gap-2 py-6 text-xs text-muted"><Spinner /> Chiedo a {storeName(store)} le app che la chiave vede…</div>
          ) : apps.error ? (
            <Alert tone="warn" title="Non riesco a leggere l'elenco delle app">
              {errorMessage(apps.error)} Puoi comunque inserire l'id a mano.
            </Alert>
          ) : (
            <div className="space-y-2">
              <div className="relative">
                <Search className="absolute top-2 left-2.5 size-4 text-faint" />
                <Input className="pl-8" placeholder="Cerca per nome o identificativo" value={filter} onChange={(e) => setFilter(e.target.value)} />
              </div>
              <ul className="max-h-72 divide-y divide-line overflow-y-auto rounded-md border border-line">
                {visible.length === 0 && <li className="px-3 py-6 text-center text-xs text-muted">Nessuna app.</li>}
                {visible.map((a) => (
                  <li key={a.externalId}>
                    <button
                      type="button"
                      disabled={link.isPending}
                      onClick={() => submit(a.externalId, a.name)}
                      className="flex w-full items-center gap-3 px-3 py-2.5 text-left hover:bg-hover"
                    >
                      <span className="min-w-0 flex-1">
                        <span className="block truncate text-[13px] text-fg">{a.name}</span>
                        <span className="block truncate font-mono text-[11px] text-faint">{a.bundleId}</span>
                      </span>
                      {store === "AppStore" && <span className="font-mono text-[11px] text-faint">{a.externalId}</span>}
                    </button>
                  </li>
                ))}
              </ul>
            </div>
          )}

          {link.error && <Alert tone="bad">{errorMessage(link.error)}</Alert>}

          <div className="flex items-center justify-between border-t border-line pt-3">
            <button type="button" className="text-xs text-muted hover:text-fg" onClick={() => setManual(!manual)}>
              {manual ? "Scegli dall'elenco" : "Inserisci l'id a mano"}
            </button>
            {manual && (
              <Button variant="primary" loading={link.isPending} disabled={!externalAppId.trim()} onClick={() => submit(externalAppId, displayName || null)}>
                Collega
              </Button>
            )}
          </div>
        </div>
      )}
    </Modal>
  );
}

function ManualAppForm(props: {
  store: Store;
  externalAppId: string;
  setExternalAppId: (v: string) => void;
  displayName: string;
  setDisplayName: (v: string) => void;
}) {
  return (
    <div className="space-y-4">
      <Field
        label={props.store === "AppStore" ? "Apple ID dell'app" : "Package name"}
        hint={
          props.store === "AppStore"
            ? "Il numero che trovi in App Store Connect, Informazioni sull'app. Non il bundle id."
            : "Per esempio com.azienda.app."
        }
      >
        <Input
          className="font-mono"
          value={props.externalAppId}
          onChange={(e) => props.setExternalAppId(e.target.value)}
          placeholder={props.store === "AppStore" ? "1234567890" : "com.azienda.app"}
        />
      </Field>
      <Field label="Nome da mostrare (facoltativo)">
        <Input value={props.displayName} onChange={(e) => props.setDisplayName(e.target.value)} />
      </Field>
    </div>
  );
}

function EditProjectModal({ orgId, project, open, onOpenChange }: { orgId: string; project: Project; open: boolean; onOpenChange: (v: boolean) => void }) {
  const navigate = useNavigate();
  const [name, setName] = useState(project.name);
  const [description, setDescription] = useState(project.description ?? "");
  const [confirmDelete, setConfirmDelete] = useState(false);

  const save = useApiMutation(
    () => ({ path: `/orgs/${orgId}/projects/${project.id}`, method: "PUT", body: { name, description: description || null } }),
    [keys.project(orgId, project.id), keys.projects(orgId)],
  );
  const remove = useApiMutation(() => ({ path: `/orgs/${orgId}/projects/${project.id}`, method: "DELETE" }), [keys.projects(orgId)]);

  return (
    <Modal
      open={open}
      onOpenChange={onOpenChange}
      title="Modifica progetto"
      footer={
        <>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>Annulla</Button>
          <Button variant="primary" loading={save.isPending} disabled={!name.trim()} onClick={() => save.mutate(undefined, { onSuccess: () => onOpenChange(false) })}>
            Salva
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <Field label="Nome">
          <Input value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label="Descrizione">
          <Textarea rows={3} className="font-sans text-[13px]" value={description} onChange={(e) => setDescription(e.target.value)} />
        </Field>
        {(save.error || remove.error) && <Alert tone="bad">{errorMessage(save.error ?? remove.error)}</Alert>}

        <div className="rounded-md border border-bad/30 p-3">
          <p className="text-[13px] text-fg">Elimina il progetto</p>
          <p className="mt-0.5 text-xs text-muted">Le app vengono scollegate; le chiavi restano.</p>
          <div className="mt-3">
            {confirmDelete ? (
              <div className="flex gap-2">
                <Button size="sm" variant="danger" loading={remove.isPending} onClick={() => remove.mutate(undefined, { onSuccess: () => navigate(`/o/${orgId}/projects`) })}>
                  Sì, elimina
                </Button>
                <Button size="sm" variant="ghost" onClick={() => setConfirmDelete(false)}>No</Button>
              </div>
            ) : (
              <Button size="sm" variant="danger" icon={<Trash2 className="size-3" />} onClick={() => setConfirmDelete(true)}>
                Elimina
              </Button>
            )}
          </div>
        </div>
      </div>
    </Modal>
  );
}
