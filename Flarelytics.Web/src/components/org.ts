import { useOutletContext } from "react-router";
import type { OrgRole, OrgSummary } from "../api/types";

/** L'organizzazione della pagina, passata da AppShell. */
export function useOrg(): OrgSummary {
  const org = useOutletContext<OrgSummary | undefined>();
  if (!org) throw new Error("Pagina di organizzazione fuori da /o/:orgId");
  return org;
}

const dateFormat = new Intl.DateTimeFormat("it-IT", { day: "numeric", month: "short", year: "numeric" });
const dateTimeFormat = new Intl.DateTimeFormat("it-IT", { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });

export const formatDate = (iso: string) => dateFormat.format(new Date(iso));
export const formatDateTime = (iso: string) => dateTimeFormat.format(new Date(iso));
export const formatPrice = (cents: number) =>
  new Intl.NumberFormat("it-IT", { style: "currency", currency: "EUR", maximumFractionDigits: 0 }).format(cents / 100);

/*
 * Cosa mostrare a chi. Il server ricontrolla tutto: qui servono solo a non
 * offrire pagine e pulsanti che risponderebbero "non puoi".
 */

const isAdminRole = (role: OrgRole) => role === "Admin" || role === "Owner";

/** Vede tutti i progetti e tutte le sezioni. */
export const isFull = (org: OrgSummary) => org.access.allProjects && org.access.store && org.access.social;

export const hasStore = (org: OrgSummary) => org.access.store;
export const hasSocial = (org: OrgSummary) => org.access.social;

/** Gestisce l'organizzazione: membri, chiavi, account social, progetti. */
export const canManageOrg = (org: OrgSummary) => isAdminRole(org.role) && isFull(org);

/** Il progetto si vede (null = un post dell'organizzazione, senza progetto: solo chi vede tutti i progetti). */
export const canSeeProject = (org: OrgSummary, projectId: string | null) =>
  org.access.allProjects || (projectId !== null && org.access.projectIds.includes(projectId));

/** La prima pagina dell'organizzazione per questo membro: la dashboard se ha lo Store, se no il calendario. */
export const homePath = (org: OrgSummary) => (org.access.store ? `/o/${org.id}` : org.access.social ? `/o/${org.id}/social` : `/o/${org.id}/projects`);
