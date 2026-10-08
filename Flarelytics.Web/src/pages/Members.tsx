import { MailPlus, X } from "lucide-react";
import { useState } from "react";
import { useNavigate } from "react-router";
import { errorMessage } from "../api/client";
import { canAdmin, keys, useApiMutation, useInvitations, useMe, useMembers } from "../api/hooks";
import type { Member, OrgRole } from "../api/types";
import { Alert, Badge, Button, Field, Input, Modal, PageHeader, PageLoader, Panel, Select } from "../components/ui";
import { formatDate, useOrg } from "../components/org";

const roleLabel: Record<OrgRole, string> = { Owner: "Owner", Admin: "Admin", Viewer: "Lettore" };
const roleHelp: Record<OrgRole, string> = {
  Viewer: "Vede progetti e dati.",
  Admin: "Gestisce progetti, chiavi e inviti.",
  Owner: "Tutto, compresi abbonamento e altri owner.",
};

export function MembersPage() {
  const org = useOrg();
  const members = useMembers(org.id);
  const invitations = useInvitations(org.id, canAdmin(org.role));
  const [inviting, setInviting] = useState(false);

  if (members.isPending) return <PageLoader />;

  return (
    <>
      <PageHeader
        title="Membri"
        description="Chi può entrare in questa organizzazione, e con quale ruolo."
        actions={canAdmin(org.role) && <Button variant="primary" icon={<MailPlus className="size-3.5" />} onClick={() => setInviting(true)}>Invita</Button>}
      />

      <Panel className="mb-6">
        <ul className="divide-y divide-line">
          {members.data!.map((m) => <MemberRow key={m.userId} member={m} />)}
        </ul>
      </Panel>

      {canAdmin(org.role) && (invitations.data?.length ?? 0) > 0 && (
        <Panel title="Inviti in attesa">
          <ul className="divide-y divide-line">
            {invitations.data!.map((i) => <InvitationRow key={i.id} id={i.id} email={i.email} role={i.role} expires={i.expiresAtUtc} />)}
          </ul>
        </Panel>
      )}

      <InviteModal open={inviting} onOpenChange={setInviting} />
    </>
  );
}

function MemberRow({ member: m }: { member: Member }) {
  const org = useOrg();
  const me = useMe();
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
          <p className="truncate text-[13px] text-fg">
            {m.fullName} {isMe && <span className="text-faint">(tu)</span>}
          </p>
          <p className="truncate text-xs text-muted">{m.email} · dal {formatDate(m.joinedAtUtc)}</p>
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
      {(changeRole.error || remove.error) && (
        <div className="mt-2">
          <Alert tone="bad">{errorMessage(changeRole.error ?? remove.error)}</Alert>
        </div>
      )}
    </li>
  );
}

function InvitationRow({ id, email, role, expires }: { id: string; email: string; role: OrgRole; expires: string }) {
  const org = useOrg();
  const revoke = useApiMutation(() => ({ path: `/orgs/${org.id}/invitations/${id}`, method: "DELETE" }), [keys.invitations(org.id)]);

  return (
    <li className="flex items-center gap-3 px-4 py-3">
      <div className="min-w-0 flex-1">
        <p className="truncate text-[13px] text-fg">{email}</p>
        <p className="text-xs text-muted">Scade il {formatDate(expires)}</p>
      </div>
      <Badge>{roleLabel[role]}</Badge>
      <Button size="sm" variant="ghost" icon={<X className="size-3" />} loading={revoke.isPending} onClick={() => revoke.mutate()}>
        Revoca
      </Button>
    </li>
  );
}

function InviteModal({ open, onOpenChange }: { open: boolean; onOpenChange: (v: boolean) => void }) {
  const org = useOrg();
  const [email, setEmail] = useState("");
  const [role, setRole] = useState<OrgRole>("Viewer");
  const invite = useApiMutation(() => ({ path: `/orgs/${org.id}/invitations`, body: { email, role } }), [keys.invitations(org.id)]);
  const roles: OrgRole[] = org.role === "Owner" ? ["Viewer", "Admin", "Owner"] : ["Viewer", "Admin"];

  return (
    <Modal
      open={open}
      onOpenChange={(o) => {
        onOpenChange(o);
        if (!o) {
          setEmail("");
          invite.reset();
        }
      }}
      title="Invita una persona"
      description="Riceverà un link valido 7 giorni. Se non ha un account, lo crea dal link."
      footer={
        <>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>Annulla</Button>
          <Button variant="primary" loading={invite.isPending} disabled={!email.trim()} onClick={() => invite.mutate(undefined, { onSuccess: () => onOpenChange(false) })}>
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
        {invite.error && <Alert tone="bad">{errorMessage(invite.error)}</Alert>}
      </div>
    </Modal>
  );
}
