import * as Menu from "@radix-ui/react-dropdown-menu";
import clsx from "clsx";
import { ArrowLeft, AtSign, CalendarDays, ChartColumn, Check, ChevronsUpDown, FolderKanban, Inbox, KeyRound, KeySquare, LayoutDashboard, LogOut, MessageSquare, Plus, Repeat, ScrollText, ServerCog, Settings, UserRound, Users } from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import { NavLink, Outlet, useNavigate, useParams } from "react-router";
import { useApiMutation, keys, useInstance, useMe, useSocialInbox } from "../api/hooks";
import { errorMessage } from "../api/client";
import type { Me, OrgSummary } from "../api/types";
import { canManageOrg, hasSocial, hasStore, homePath, isFull } from "./org";
import { useAuth } from "../auth/AuthContext";
import { Logo } from "./Logo";
import { ProjectSwitcher, ProjectSidebarNav, projectSections } from "./ProjectNav";
import { ThemeSwitcher } from "./ThemeSwitcher";
import { useScale, type Scale } from "../scale";
import { Alert, Button, Field, Input, Modal, PageLoader } from "./ui";

const menuItem =
  "flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-[0.8125rem] text-muted outline-none data-[highlighted]:bg-hover data-[highlighted]:text-fg";
const menuContent = "z-50 min-w-56 rounded-md border border-line-strong bg-panel p-1 shadow-xl";

/** La cornice di tutte le pagine interne: barra laterale con organizzazione e sezioni, contenuto a destra. */
export function AppShell() {
  const { orgId, projectId, tab } = useParams();
  const me = useMe();

  // Fuori da /o/:orgId (per esempio in /account) la barra laterale resta
  // sull'ultima organizzazione aperta, invece di svuotarsi.
  useEffect(() => {
    if (orgId) rememberOrg(orgId);
  }, [orgId]);

  if (me.isPending) return <PageLoader />;
  if (!me.data) return null;

  const orgs = me.data.organizations;
  const org = orgs.find((o) => o.id === orgId);
  const navOrg = org ?? orgs.find((o) => o.id === lastOrg()) ?? orgs[0];

  return (
    <div className="flex min-h-full">
      {/* Alta quanto lo schermo: in cima l'organizzazione, in fondo l'utente, e
          in mezzo le voci, che scorrono quando non ci stanno (schermi bassi,
          dimensione "grande"). */}
      <aside className="sticky top-0 hidden h-screen w-60 shrink-0 flex-col border-r border-line bg-panel md:flex">
        <div className="flex h-16 shrink-0 items-center border-b border-line px-4">
          <Logo />
        </div>
        {/* Dentro un progetto tutta la barra diventa del progetto: chiavi,
            membri e impostazioni dell'organizzazione stanno nella vista globale. */}
        {org && projectId ? (
          <ProjectSidebarNav org={org} projectId={projectId} section={tab} />
        ) : (
        <>
        <div className="shrink-0 border-b border-line p-2">
          <OrgSwitcher me={me.data} currentId={navOrg?.id} />
        </div>
        {navOrg && (
          <nav className="min-h-0 flex-1 space-y-0.5 overflow-y-auto p-2">
            {/* Il menu segue l'accesso del membro (progetti e sezioni); il server ricontrolla comunque. */}
            <NavItem to={`/o/${navOrg.id}`} end icon={<LayoutDashboard className="size-4" />}>Panoramica</NavItem>
            <NavItem to={`/o/${navOrg.id}/projects`} icon={<FolderKanban className="size-4" />}>Progetti</NavItem>
            {hasStore(navOrg) && <NavItem to={`/o/${navOrg.id}/analytics`} icon={<ChartColumn className="size-4" />}>Download</NavItem>}
            {hasStore(navOrg) && <NavItem to={`/o/${navOrg.id}/reviews`} icon={<MessageSquare className="size-4" />}>Recensioni</NavItem>}
            {hasStore(navOrg) && isFull(navOrg) && <NavItem to={`/o/${navOrg.id}/credentials`} icon={<KeyRound className="size-4" />}>Chiavi degli store</NavItem>}
            {hasSocial(navOrg) && (
              <>
                <p className="px-2 pt-4 pb-1 text-[0.6875rem] font-medium tracking-wide text-faint uppercase">Social</p>
                <NavItem to={`/o/${navOrg.id}/social`} end icon={<CalendarDays className="size-4" />}>Calendario</NavItem>
                <NavItem to={`/o/${navOrg.id}/social/inbox`} icon={<Inbox className="size-4" />}>
                  Da programmare <InboxCount orgId={navOrg.id} />
                </NavItem>
                <NavItem to={`/o/${navOrg.id}/social/recurring`} icon={<Repeat className="size-4" />}>Post ricorrenti</NavItem>
                <NavItem to={`/o/${navOrg.id}/social/accounts`} icon={<AtSign className="size-4" />}>Account social</NavItem>
              </>
            )}
            {canManageOrg(navOrg) && (
              <>
                <p className="px-2 pt-4 pb-1 text-[0.6875rem] font-medium tracking-wide text-faint uppercase">Organizzazione</p>
                <NavItem to={`/o/${navOrg.id}/members`} icon={<Users className="size-4" />}>Membri</NavItem>
                <NavItem to={`/o/${navOrg.id}/api-keys`} icon={<KeySquare className="size-4" />}>Chiavi API</NavItem>
                <NavItem to={`/o/${navOrg.id}/logs`} icon={<ScrollText className="size-4" />}>Log</NavItem>
                <NavItem to={`/o/${navOrg.id}/settings`} icon={<Settings className="size-4" />}>Impostazioni</NavItem>
              </>
            )}
          </nav>
        )}
        </>
        )}
        <div className="mt-auto shrink-0 border-t border-line p-2">
          <UserMenu me={me.data} />
        </div>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <MobileBar me={me.data} org={navOrg} projectId={org ? projectId : undefined} section={tab} />
        <main className="mx-auto w-full max-w-6xl flex-1 px-4 py-8 md:px-8">
          <Outlet context={org} />
        </main>
      </div>
    </div>
  );
}

const lastOrgKey = "flarelytics.lastOrg";

/** Solo una comodità: se lo storage non c'è (navigazione privata) si riparte dalla prima organizzazione. */
function rememberOrg(id: string) {
  try {
    localStorage.setItem(lastOrgKey, id);
  } catch {
    /* niente */
  }
}

export function lastOrg(): string | null {
  try {
    return localStorage.getItem(lastOrgKey);
  } catch {
    return null;
  }
}

/** Tre misure del pannello: la "A" più piccola o più grande dice cosa fa ciascuna. */
function ScaleSwitcher() {
  const { scale, setScale } = useScale();
  const options: { value: Scale; label: string; size: string }[] = [
    { value: "compact", label: "Compatta", size: "text-[0.6875rem]" },
    { value: "normal", label: "Normale", size: "text-[0.8125rem]" },
    { value: "large", label: "Grande", size: "text-[1rem]" },
  ];
  return (
    <div className="inline-flex rounded-md border border-line-strong bg-field p-0.5" role="radiogroup" aria-label="Dimensione del pannello">
      {options.map((o) => (
        <button key={o.value} type="button" role="radio" aria-checked={scale === o.value} title={o.label}
          onClick={(e) => { e.preventDefault(); setScale(o.value); }}
          className={clsx("flex size-7 items-center justify-center rounded font-medium", o.size, scale === o.value ? "bg-panel-2 text-fg shadow-sm" : "text-muted hover:text-fg")}>
          A
        </button>
      ))}
    </div>
  );
}

/** Quanti post aspettano nella coda: si vede dalla barra senza aprire la pagina. */
function InboxCount({ orgId }: { orgId: string }) {
  const inbox = useSocialInbox(orgId);
  const count = inbox.data?.length ?? 0;
  if (count === 0) return null;
  return <span className="ml-auto rounded-full bg-brand/15 px-1.5 text-[0.6875rem] font-medium text-brand-fg">{count}</span>;
}

function NavItem({ to, icon, children, end }: { to: string; icon: ReactNode; children: ReactNode; end?: boolean }) {
  return (
    <NavLink
      to={to}
      end={end}
      className={({ isActive }) =>
        clsx(
          "flex h-8 items-center gap-2.5 rounded-md px-2 text-[0.8125rem] transition-colors",
          isActive ? "bg-hover text-fg" : "text-muted hover:bg-hover/60 hover:text-fg",
        )
      }
    >
      {icon}
      {children}
    </NavLink>
  );
}

function OrgSwitcher({ me, currentId }: { me: Me; currentId?: string }) {
  const navigate = useNavigate();
  const [creating, setCreating] = useState(false);
  const current = me.organizations.find((o) => o.id === currentId);

  return (
    <>
      <Menu.Root>
        <Menu.Trigger className="flex h-9 w-full items-center gap-2 rounded-md border border-line-strong bg-panel-2 px-2.5 text-left hover:bg-hover">
          <span className="flex size-5 items-center justify-center rounded bg-brand/15 text-[0.6875rem] font-semibold text-brand-fg">
            {(current?.name ?? "?").charAt(0).toUpperCase()}
          </span>
          <span className="min-w-0 flex-1 truncate text-[0.8125rem] text-fg">{current?.name ?? "Scegli organizzazione"}</span>
          <ChevronsUpDown className="size-3.5 text-faint" />
        </Menu.Trigger>
        <Menu.Portal>
          <Menu.Content align="start" sideOffset={4} className={menuContent}>
            <Menu.Label className="px-2 py-1 text-[0.6875rem] text-faint">Organizzazioni</Menu.Label>
            {me.organizations.map((o) => (
              <Menu.Item key={o.id} className={menuItem} onSelect={() => navigate(`/o/${o.id}`)}>
                <span className="min-w-0 flex-1 truncate">{o.name}</span>
                {o.id === currentId && <Check className="size-3.5 text-brand-fg" />}
              </Menu.Item>
            ))}
            <Menu.Separator className="my-1 h-px bg-line" />
            <Menu.Item className={menuItem} onSelect={() => setCreating(true)}>
              <Plus className="size-3.5" /> Nuova organizzazione
            </Menu.Item>
          </Menu.Content>
        </Menu.Portal>
      </Menu.Root>
      <CreateOrgModal open={creating} onOpenChange={setCreating} />
    </>
  );
}

function CreateOrgModal({ open, onOpenChange }: { open: boolean; onOpenChange: (v: boolean) => void }) {
  const navigate = useNavigate();
  const [name, setName] = useState("");
  const create = useApiMutation<string, { id: string }>((n) => ({ path: "/orgs", body: { name: n } }), [keys.me]);

  return (
    <Modal
      open={open}
      onOpenChange={onOpenChange}
      title="Nuova organizzazione"
      description="Un'organizzazione ha i suoi progetti, le sue chiavi e i suoi membri. Serve, per esempio, se segui le app di più clienti."
      footer={
        <>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>Annulla</Button>
          <Button
            variant="primary"
            loading={create.isPending}
            disabled={!name.trim()}
            onClick={() =>
              create.mutate(name, {
                onSuccess: (org) => {
                  onOpenChange(false);
                  setName("");
                  navigate(`/o/${org.id}`);
                },
              })
            }
          >
            Crea
          </Button>
        </>
      }
    >
      <div className="space-y-3">
        <Field label="Nome">
          <Input autoFocus value={name} onChange={(e) => setName(e.target.value)} placeholder="Acme S.r.l." />
        </Field>
        {create.error && <Alert tone="bad">{errorMessage(create.error)}</Alert>}
      </div>
    </Modal>
  );
}

function UserMenu({ me }: { me: Me }) {
  const instance = useInstance();
  const { signOut } = useAuth();
  const navigate = useNavigate();

  return (
    <Menu.Root>
      <Menu.Trigger className="flex w-full items-center gap-2.5 rounded-md px-2 py-1.5 text-left hover:bg-hover">
        <span className="flex size-7 items-center justify-center rounded-full border border-line-strong bg-panel-2 text-xs font-medium text-fg">
          {me.fullName.charAt(0).toUpperCase()}
        </span>
        <span className="min-w-0 flex-1">
          <span className="block truncate text-[0.8125rem] text-fg">{me.fullName}</span>
          <span className="block truncate text-[0.6875rem] text-faint">{me.email}</span>
        </span>
      </Menu.Trigger>
      <Menu.Portal>
        <Menu.Content side="top" align="start" sideOffset={6} className={menuContent}>
          <Menu.Item className={menuItem} onSelect={() => navigate("/account")}>
            <UserRound className="size-3.5" /> Il tuo account
          </Menu.Item>
          {me.isInstanceAdmin && (
            <Menu.Item className={menuItem} onSelect={() => navigate("/instance")}>
              <ServerCog className="size-3.5" /> Impostazioni dell'istanza
            </Menu.Item>
          )}
          <Menu.Separator className="my-1 h-px bg-line" />
          <div className="flex items-center justify-between gap-3 px-2 py-1.5">
            <span className="text-[0.8125rem] text-muted">Tema</span>
            <ThemeSwitcher compact />
          </div>
          <div className="flex items-center justify-between gap-3 px-2 py-1.5">
            <span className="text-[0.8125rem] text-muted">Dimensione</span>
            <ScaleSwitcher />
          </div>
          <Menu.Separator className="my-1 h-px bg-line" />
          <Menu.Item className={menuItem} onSelect={() => signOut().then(() => navigate("/login"))}>
            <LogOut className="size-3.5" /> Esci
          </Menu.Item>
          {instance.data && <p className="px-2 pt-1.5 pb-0.5 text-[0.6875rem] text-faint">ElephantSight {instance.data.version}</p>}
        </Menu.Content>
      </Menu.Portal>
    </Menu.Root>
  );
}

/** Sotto i 768px la barra laterale non c'è: le stesse cose stanno in una barra in alto. */
function MobileBar({ me, org, projectId, section }: { me: Me; org?: OrgSummary; projectId?: string; section?: string }) {
  const link = ({ isActive }: { isActive: boolean }) => clsx("rounded px-2 py-1 whitespace-nowrap", isActive ? "bg-hover text-fg" : "text-muted");
  const orgId = org?.id;

  if (orgId && projectId) {
    return (
      <div className="border-b border-line bg-panel md:hidden">
        <div className="flex items-center gap-2 px-3 py-2">
          <NavLink to={homePath(org!)} className="rounded p-1.5 text-muted hover:text-fg" aria-label="Vista globale"><ArrowLeft className="size-4" /></NavLink>
          <div className="min-w-0 flex-1"><ProjectSwitcher orgId={orgId} projectId={projectId} section={section} /></div>
        </div>
        <nav className="flex gap-1 overflow-x-auto px-3 pb-2 text-[0.8125rem]">
          {projectSections(`/o/${orgId}/projects/${projectId}`, org!).map((s) => <NavLink key={s.to} to={s.to} end={s.end} className={link}>{s.label}</NavLink>)}
        </nav>
      </div>
    );
  }

  return (
    <div className="border-b border-line bg-panel md:hidden">
      <div className="flex h-14 items-center justify-between gap-2 px-4">
        <Logo />
        <div className="w-48">
          <OrgSwitcher me={me} currentId={orgId} />
        </div>
      </div>
      {org && (
        <nav className="flex gap-1 overflow-x-auto px-3 pb-2 text-[0.8125rem]">
          {([
            ["", "Panoramica", true],
            ["/projects", "Progetti", true],
            ["/analytics", "Download", hasStore(org)],
            ["/reviews", "Recensioni", hasStore(org)],
            ["/credentials", "Chiavi", hasStore(org) && isFull(org)],
            ["/social", "Calendario", hasSocial(org)],
            ["/social/inbox", "Da programmare", hasSocial(org)],
            ["/social/recurring", "Ricorrenti", hasSocial(org)],
            ["/social/accounts", "Social", hasSocial(org)],
            ["/members", "Membri", canManageOrg(org)],
            ["/api-keys", "Chiavi API", canManageOrg(org)],
            ["/logs", "Log", canManageOrg(org)],
            ["/settings", "Impostazioni", canManageOrg(org)],
          ] as const).filter(([, , visible]) => visible).map(([path, label]) => (
            <NavLink key={path} to={`/o/${orgId}${path}`} end={path === "" || path === "/social"} className={link}>{label}</NavLink>
          ))}
          <NavLink to="/account" className={link}>Account</NavLink>
          {me.isInstanceAdmin && <NavLink to="/instance" className={link}>Istanza</NavLink>}
        </nav>
      )}
    </div>
  );
}
