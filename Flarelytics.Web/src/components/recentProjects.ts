/*
 * Gli ultimi progetti aperti, per organizzazione: una comodità di questo
 * browser (localStorage), non un dato da condividere. Senza storage
 * (finestra privata) la panoramica mostra semplicemente i primi progetti.
 */

const key = (orgId: string) => `elephantsight.recentProjects.${orgId}`;
const MAX = 6;

export function recentProjects(orgId: string): string[] {
  try {
    const raw = localStorage.getItem(key(orgId));
    return raw ? (JSON.parse(raw) as string[]).slice(0, MAX) : [];
  } catch {
    return [];
  }
}

export function rememberProject(orgId: string, projectId: string) {
  try {
    localStorage.setItem(key(orgId), JSON.stringify([projectId, ...recentProjects(orgId).filter((id) => id !== projectId)].slice(0, MAX)));
  } catch {
    /* senza storage non si ricorda niente */
  }
}
