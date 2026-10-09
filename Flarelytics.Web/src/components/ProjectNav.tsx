import * as Menu from "@radix-ui/react-dropdown-menu";
import clsx from "clsx";
import { ArrowLeft, CalendarDays, Check, ChevronsUpDown, FileKey2, LayoutDashboard, MessageSquare, Package, Repeat, Settings, Store as StoreIcon } from "lucide-react";
import type { ReactNode } from "react";
import { NavLink, useNavigate } from "react-router";
import { useProjects } from "../api/hooks";
import type { OrgSummary, Project } from "../api/types";
import { canManageOrg, hasSocial, hasStore, homePath } from "./org";
import { AppIcon } from "./AppIcon";
import { StoreGlyph } from "./StoreIcons";

const menuItem =
  "flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-[0.8125rem] text-muted outline-none data-[highlighted]:bg-hover data-[highlighted]:text-fg";

/**
 * Le sezioni di un progetto: le stesse nella barra laterale e in quella per il
 * telefono, secondo le sezioni che il membro vede. Senza Store la panoramica è
 * il calendario del progetto.
 */
export const projectSections = (base: string, org: OrgSummary) => [
  ...(hasStore(org)
    ? [
        { to: base, label: "Panoramica", icon: <LayoutDashboard className="size-4" />, end: true },
        { to: `${base}/releases`, label: "Versioni e build", icon: <Package className="size-4" /> },
        { to: `${base}/reviews`, label: "Recensioni", icon: <MessageSquare className="size-4" /> },
        { to: `${base}/listing`, label: "Pagina dello store", icon: <StoreIcon className="size-4" /> },
        { to: `${base}/files`, label: "File di firma", icon: <FileKey2 className="size-4" /> },
      ]
    : []),
  ...(hasSocial(org)
    ? [
        { to: hasStore(org) ? `${base}/social` : base, label: "Calendario social", icon: <CalendarDays className="size-4" />, end: !hasStore(org) },
        { to: `${base}/recurring`, label: "Post ricorrenti", icon: <Repeat className="size-4" /> },
      ]
    : []),
  ...(hasStore(org) || canManageOrg(org) ? [{ to: `${base}/settings`, label: "Impostazioni", icon: <Settings className="size-4" /> }] : []),
];

/** I due segni degli store: pieni se l'app è collegata, sbiaditi se no. */
export function StoreDots({ project }: { project: Project }) {
  return (
    <span className="flex gap-1">
      {(["AppStore", "GooglePlay"] as const).map((s) => (
        <StoreGlyph key={s} store={s} className={clsx("size-3.5", project.apps.some((a) => a.store === s) ? (s === "AppStore" ? "text-ios" : "text-android") : "text-faint opacity-40")} />
      ))}
    </span>
  );
}

/**
 * Il selettore del progetto: si passa da un progetto all'altro restando nella
 * stessa sezione (dalle recensioni di uno alle recensioni dell'altro).
 */
export function ProjectSwitcher({ orgId, projectId, section }: { orgId: string; projectId: string; section?: string }) {
  const navigate = useNavigate();
  const projects = useProjects(orgId);
  const current = projects.data?.find((p) => p.id === projectId);

  return (
    <Menu.Root>
      <Menu.Trigger className="flex h-11 w-full items-center gap-2.5 rounded-md border border-line-strong bg-panel-2 px-2 text-left hover:bg-hover">
        <AppIcon src={current?.iconUrl} name={current?.name ?? "?"} size="sm" className="!size-7" />
        <span className="min-w-0 flex-1">
          <span className="block truncate text-[0.8125rem] font-medium text-fg">{current?.name ?? "…"}</span>
          {current && <StoreDots project={current} />}
        </span>
        <ChevronsUpDown className="size-3.5 text-faint" />
      </Menu.Trigger>
      <Menu.Portal>
        <Menu.Content align="start" sideOffset={4} className="z-50 max-h-96 min-w-60 overflow-y-auto rounded-md border border-line-strong bg-panel p-1 shadow-xl">
          <Menu.Label className="px-2 py-1 text-[0.6875rem] text-faint">Progetti</Menu.Label>
          {projects.data?.map((p) => (
            <Menu.Item key={p.id} className={menuItem} onSelect={() => navigate(`/o/${orgId}/projects/${p.id}${section ? `/${section}` : ""}`)}>
              <AppIcon src={p.iconUrl} name={p.name} size="sm" className="!size-5 !rounded" />
              <span className="min-w-0 flex-1 truncate">{p.name}</span>
              {p.id === projectId && <Check className="size-3.5 text-brand-fg" />}
            </Menu.Item>
          ))}
          <Menu.Separator className="my-1 h-px bg-line" />
          <Menu.Item className={menuItem} onSelect={() => navigate(`/o/${orgId}/projects`)}>Tutti i progetti</Menu.Item>
        </Menu.Content>
      </Menu.Portal>
    </Menu.Root>
  );
}

/** La barra laterale dentro un progetto: si torna alla vista globale, si cambia progetto, si naviga fra le sue sezioni. */
export function ProjectSidebarNav({ org, projectId, section }: { org: OrgSummary; projectId: string; section?: string }) {
  const { id: orgId, name: orgName } = org;
  const base = `/o/${orgId}/projects/${projectId}`;
  return (
    <>
      <div className="space-y-2 border-b border-line p-2">
        <NavLink to={homePath(org)} className="flex h-7 items-center gap-1.5 rounded-md px-2 text-xs text-muted hover:bg-hover hover:text-fg">
          <ArrowLeft className="size-3.5" />
          <span className="truncate">Vista globale · {orgName}</span>
        </NavLink>
        <ProjectSwitcher orgId={orgId} projectId={projectId} section={section} />
      </div>
      <nav className="flex-1 space-y-0.5 p-2">
        {projectSections(base, org).map((s) => <SectionLink key={s.to} to={s.to} icon={s.icon} end={s.end}>{s.label}</SectionLink>)}
      </nav>
    </>
  );
}

function SectionLink({ to, icon, children, end }: { to: string; icon: ReactNode; children: ReactNode; end?: boolean }) {
  return (
    <NavLink
      to={to}
      end={end}
      className={({ isActive }) =>
        clsx("flex h-8 items-center gap-2.5 rounded-md px-2 text-[0.8125rem] transition-colors", isActive ? "bg-hover text-fg" : "text-muted hover:bg-hover/60 hover:text-fg")
      }
    >
      {icon}
      {children}
    </NavLink>
  );
}
