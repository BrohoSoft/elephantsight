import { useOutletContext } from "react-router";
import type { OrgSummary } from "../api/types";

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
