import { FolderKanban, MailPlus, X } from "lucide-react";
import { useState } from "react";
import { useNavigate } from "react-router";
import { errorMessage } from "../api/client";
import { canAdmin, keys, useApiMutation, useInvitations, useMe, useMembers, useProjects } from "../api/hooks";
import type { CreatedInvitation, Member, MemberAccess, OrgRole, Project } from "../api/types";
import { Alert, Badge, Button, CopyButton, Field, Input, Modal, PageHeader, PageLoader, Panel, Select } from "../components/ui";
import { formatDate, useOrg } from "../components/org";

const roleLabel: Record<OrgRole, string> = { Owner: "Owner", Admin: "Admin", Viewer: "Lettore" };
const roleHelp: Record<OrgRole, string> = {
  Viewer: "Vede i progetti e le sezioni che scegli qui sotto, senza cambiare niente.",
  Admin: "Lavora nei progetti e nelle sezioni che scegli (post, recensioni, build…). Con tutti i progetti e tutte le sezioni gestisce anche membri, chiavi, account social e progetti.",
  Owner: "Tutto, sempre: vede tutti i progetti e nomina gli altri owner.",
};

const fullAccess: MemberAccess = { allProjects: true, projectIds: [], store: true, social: true };

/** L'accesso in poche parole: "Cliente A, Cliente B · solo Social". */
function describeAccess(a: MemberAccess, projects: Project[]): string {
  const where = a.allProjects ? "Tutti i progetti" : projects.filter((p) => a.projectIds.includes(p.id)).map((p) => p.name).join(", ") || "Nessun progetto";
  const what = a.store && a.social ? "Store e Social" : a.store ? "solo Store" : "solo Social";
  return `${where} · ${what}`;
}

/** Come lo vuole l'API: le sezioni come "Store, Social". */
const accessBody = (a: MemberAccess) => ({
  allProjects: a.allProjects,
  projectIds: a.allProjects ? [] : a.projectIds,
  sections: [a.store && "Store", a.social && "Social"].filter(Boolean).join(", "),
});

const accessIncomplete = (a: MemberAccess) => (!a.store && !a.social) || (!a.allProjects && a.projectIds.length === 0);

/**
 * Progetti e sezioni di un membro: per dare a un cliente solo il suo progetto,
 * o a chi segue i social solo il calendario, senza lo Store.
 */
function AccessEditor({ value, onChange, projects }: { value: MemberAccess; onChange: (a: MemberAccess) => void; projects: Project[] }) {
  const toggleProject = (id: string) =>
    onChange({ ...value, projectIds: value.projectIds.includes(id) ? value.projectIds.filter((x) => x !== id) : [...value.projectIds, id] });
  const radio = "accent-brand";
  return (
    <div className="space-y-4">
      <Field label="Progetti">
        <div className="space-y-1.5 text-[0.8125rem] text-fg">
          <label className="flex items-center gap-2"><input type="radio" className={radio} checked={value.allProjects} onChange={() => onChange({ ...value, allProjects: true })} /> Tutti, anche quelli creati dopo</label>
          <label className="flex items-center gap-2"><input type="radio" className={radio} checked={!value.allProjects} onChange={() => onChange({ ...value, allProjects: false })} /> Solo questi:</label>
          {!value.allProjects && (
            <ul className="ml-6 space-y-1">
              {projects.length === 0 && <li className="text-xs text-muted">Nessun progetto: creane uno prima.</li>}
              {projects.map((p) => (
                <li key={p.id}>
                  <label className="flex items-center gap-2"><input type="checkbox" className={radio} checked={value.projectIds.includes(p.id)} onChange={() => toggleProject(p.id)} /> {p.name}</label>
                </li>
              ))}
            </ul>
          )}
        </div>
      </Field>
      <Field label="Sezioni" hint="Senza lo Store non vede dashboard, recensioni, versioni, pagina dello store e file di firma.">
        <div className="flex gap-4 text-[0.8125rem] text-fg">
          <label className="flex items-center gap-2"><input type="checkbox" className={radio} checked={value.store} onChange={(e) => onChange({ ...value, store: e.target.checked })} /> Store</label>
          <label className="flex items-center gap-2"><input type="checkbox" className={radio} checked={value.social} onChange={(e) => onChange({ ...value, social: e.target.checked })} /> Social</label>
        </div>
      </Field>
    </div>
  );
}

export function MembersPage() {
  const org = useOrg();
  const members = useMembers(org.id);
  const projects = useProjects(org.id);
  const invitations = useInvitations(org.id, canAdmin(org.role));
  const [inviting, setInviting] = useState(false);

  if (members.isPending || projects.isPending) return <PageLoader />;

  return (
    <>
      <PageHeader
        title="Membri"
        description="Chi può entrare in questa organizzazione, con quale ruolo, e quali progetti e sezioni vede."
        actions={canAdmin(org.role) && <Button variant="primary" icon={<MailPlus className="size-3.5" />} onClick={() => setInviting(true)}>Invita</Button>}
      />

      <Panel className="mb-6">
        <ul className="divide-y divide-line">
          {members.data!.map((m) => <MemberRow key={m.userId} member={m} projects={projects.data ?? []} />)}
        </ul>
      </Panel>

      {canAdmin(org.role) && (invitations.data?.length ?? 0) > 0 && (
        <Panel title="Inviti in attesa">
          <ul className="divide-y divide-line">
            {invitations.data!.map((i) => <InvitationRow key={i.id} id={i.id} email={i.email} role={i.role} access={describeAccess(i.access, projects.data ?? [])} expires={i.expiresAtUtc} />)}
          </ul>
        </Panel>
      )}

      <InviteModal open={inviting} onOpenChange={setInviting} projects={projects.data ?? []} />
    </>
  );
}

function MemberRow({ member: m, projects }: { member: Member; projects: Project[] }) {
  const org = useOrg();
  const me = useMe();
  const [editingAccess, setEditingAccess] = useState(false);
  const navigate = useNavigate();
  const isMe = me.data?.id === m.userId;

  const changeRole = useApiMutation<OrgRole>((role) => ({ path: `/orgs/${org.id}/members/${m.userId}`, method: "PUT", body: { role } }), [keys.members(org.id)]);
  const remove = useApiMutation(() => ({ path: `/orgs/${org.id}/members/${m.userId}`, method: "DELETE" }), [keys.members(org.id), keys.me]);

  const canRemove = isMe || (canAdmin(org.role) && (m.role !== "Owner" || org.role === "Owner"));

  return (
    <li className="px-4 py-3">
      <div className="flex flex-wrap items-center gap-3">
        <span className="flex size-8 shrink-0 items-center justify-center rounded-full border border-line-strong bg-panel-2 text-xs text-fg">
          {m.fullName.charAt(0).toUpperCase()}
        </span>
        <div className="min-w-0 flex-1">
          <p className="truncate text-[0.8125rem] text-fg">
            {m.fullName} {isMe && <span className="text-faint">(tu)</span>}
          </p>
          <p className="truncate text-xs text-muted">{m.email} · dal {formatDate(m.joinedAtUtc)}</p>
          <p className="mt-0.5 flex flex-wrap items-center gap-1.5 text-xs text-muted">
            <FolderKanban className="size-3 text-faint" /> {describeAccess(m.access, projects)}
            {canAdmin(org.role) && m.role !== "Owner" && (
              <button type="button" className="text-brand-fg hover:underline" onClick={() => setEditingAccess(true)}>Cambia</button>
            )}
          </p>
        </div>
        {org.role === "Owner" ? (
          <Select className="w-32" value={m.role} disabled={changeRole.isPending} onChange={(e) => changeRole.mutate(e.target.value as OrgRole)}>
            {(["Viewer", "Admin", "Owner"] as OrgRole[]).map((r) => <option key={r} value={r}>{roleLabel[r]}</option>)}
          </Select>
        ) : (
          <Badge tone={m.role === "Owner" ? "brand" : "neutral"}>{roleLabel[m.role]}</Badge>
        )}
        {canRemove && (
          <Button
            size="sm"
            variant="ghost"
            loading={remove.isPending}
            onClick={() => remove.mutate(undefined, { onSuccess: () => isMe && navigate("/") })}
          >
            {isMe ? "Esci" : "Rimuovi"}
          </Button>
        )}
      </div>
      {editingAccess && <AccessModal member={m} projects={projects} onClose={() => setEditingAccess(false)} />}
      {(changeRole.error || remove.error) && (
        <div className="mt-2">
          <Alert tone="bad">{errorMessage(changeRole.error ?? remove.error)}</Alert>
        </div>
      )}
    </li>
  );
}

function InvitationRow({ id, email, role, access, expires }: { id: string; email: string; role: OrgRole; access: string; expires: string }) {
  const org = useOrg();
  const revoke = useApiMutation(() => ({ path: `/orgs/${org.id}/invitations/${id}`, method: "DELETE" }), [keys.invitations(org.id)]);

  return (
    <li className="flex items-center gap-3 px-4 py-3">
      <div className="min-w-0 flex-1">
        <p className="truncate text-[0.8125rem] text-fg">{email}</p>
        <p className="text-xs text-muted">{access} · scade il {formatDate(expires)}</p>
      </div>
      <Badge>{roleLabel[role]}</Badge>
      <Button size="sm" variant="ghost" icon={<X className="size-3" />} loading={revoke.isPending} onClick={() => revoke.mutate()}>
        Revoca
      </Button>
    </li>
  );
}

function InviteModal({ open, onOpenChange, projects }: { open: boolean; onOpenChange: (v: boolean) => void; projects: Project[] }) {
  const org = useOrg();
  const [email, setEmail] = useState("");
  const [role, setRole] = useState<OrgRole>("Viewer");
  const [access, setAccess] = useState<MemberAccess>(fullAccess);
  const [created, setCreated] = useState<CreatedInvitation | null>(null);
  const invite = useApiMutation<void, CreatedInvitation>(
    () => ({ path: `/orgs/${org.id}/invitations`, body: { email, role, access: role === "Owner" ? null : accessBody(access) } }),
    [keys.invitations(org.id)],
  );

  // Dopo l'invio: il link si vede solo adesso. Senza email configurata è
  // l'unico modo di farlo arrivare.
  if (created) {
    return (
      <Modal
        open={open}
        onOpenChange={(o) => { onOpenChange(o); if (!o) { setCreated(null); setEmail(""); invite.reset(); } }}
        title="Invito creato"
        footer={<Button variant="primary" onClick={() => { onOpenChange(false); setCreated(null); setEmail(""); }}>Fatto</Button>}
      >
        <div className="space-y-4">
          {created.emailSent
            ? <Alert tone="ok">Abbiamo mandato il link a {created.email}. Puoi anche copiarlo e mandarlo tu.</Alert>
            : <Alert tone="info" title="Manda tu il link">L'email non è configurata su questa installazione: copia il link e mandalo a {created.email}.</Alert>}
          <div className="flex items-center gap-2 rounded-md border border-line bg-panel-2 px-3 py-2">
            <span className="min-w-0 flex-1 truncate font-mono text-xs text-fg">{created.link}</span>
            <CopyButton value={created.link} />
          </div>
          <p className="text-xs text-faint">Vale 7 giorni, solo per {created.email}.</p>
        </div>
      </Modal>
    );
  }
  const roles: OrgRole[] = org.role === "Owner" ? ["Viewer", "Admin", "Owner"] : ["Viewer", "Admin"];

  return (
    <Modal
      open={open}
      onOpenChange={(o) => {
        onOpenChange(o);
        if (!o) {
          setEmail("");
          setAccess(fullAccess);
          invite.reset();
        }
      }}
      title="Invita una persona"
      description="Si crea un link valido 7 giorni, che apre l'account o lo collega a quello che ha già."
      footer={
        <>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>Annulla</Button>
          <Button variant="primary" loading={invite.isPending} disabled={!email.trim() || (role !== "Owner" && accessIncomplete(access))}
            onClick={() => invite.mutate(undefined, { onSuccess: setCreated })}>
            Manda l'invito
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <Field label="Email">
          <Input type="email" autoFocus value={email} onChange={(e) => setEmail(e.target.value)} placeholder="collega@azienda.it" />
        </Field>
        <Field label="Ruolo" hint={roleHelp[role]}>
          <Select value={role} onChange={(e) => setRole(e.target.value as OrgRole)}>
            {roles.map((r) => <option key={r} value={r}>{roleLabel[r]}</option>)}
          </Select>
        </Field>
        {role !== "Owner" && <AccessEditor value={access} onChange={setAccess} projects={projects} />}
        {invite.error && <Alert tone="bad">{errorMessage(invite.error)}</Alert>}
      </div>
    </Modal>
  );
}

/** Cambia progetti e sezioni di un membro (non di un owner, che vede sempre tutto). */
function AccessModal({ member: m, projects, onClose }: { member: Member; projects: Project[]; onClose: () => void }) {
  const org = useOrg();
  const [access, setAccess] = useState<MemberAccess>(m.access);
  const save = useApiMutation(() => ({ path: `/orgs/${org.id}/members/${m.userId}/access`, method: "PUT", body: accessBody(access) }), [keys.members(org.id)]);

  return (
    <Modal open onOpenChange={(o) => !o && onClose()} title={`Cosa vede ${m.fullName}`}
      description="Vale da subito, anche per le sessioni già aperte."
      footer={<><Button variant="ghost" onClick={onClose}>Annulla</Button>
        <Button variant="primary" loading={save.isPending} disabled={accessIncomplete(access)} onClick={() => save.mutate(undefined, { onSuccess: onClose })}>Salva</Button></>}>
      <div className="space-y-4">
        <AccessEditor value={access} onChange={setAccess} projects={projects} />
        {save.error && <Alert tone="bad">{errorMessage(save.error)}</Alert>}
      </div>
    </Modal>
  );
}
