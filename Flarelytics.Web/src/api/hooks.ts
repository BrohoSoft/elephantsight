import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { request } from "./client";
import type {
  Metrics,
  Credential,
  InstanceInfo,
  Invitation,
  Me,
  Member,
  OrgRole,
  Project,
  StoreApp,
} from "./types";

export const keys = {
  me: ["me"] as const,
  instance: ["instance"] as const,
  org: (orgId: string) => ["org", orgId] as const,
  metrics: (orgId: string) => ["org", orgId, "metrics"] as const,
  projects: (orgId: string) => ["org", orgId, "projects"] as const,
  project: (orgId: string, id: string) => ["org", orgId, "projects", id] as const,
  credentials: (orgId: string) => ["org", orgId, "credentials"] as const,
  credentialApps: (orgId: string, id: string) => ["org", orgId, "credentials", id, "apps"] as const,
  members: (orgId: string) => ["org", orgId, "members"] as const,
  invitations: (orgId: string) => ["org", orgId, "invitations"] as const,
};

export const useMe = (enabled = true) => useQuery({ queryKey: keys.me, queryFn: () => request<Me>("/me"), enabled });
export const useInstance = () =>
  useQuery({ queryKey: keys.instance, queryFn: () => request<InstanceInfo>("/instance", { anonymous: true }), staleTime: 60_000 });

export const useMetrics = (orgId: string, days: number, projectId?: string) =>
  useQuery({
    queryKey: [...keys.metrics(orgId), days, projectId ?? "all"],
    queryFn: () => request<Metrics>(`/orgs/${orgId}/metrics?days=${days}${projectId ? `&projectId=${projectId}` : ""}`),
    placeholderData: (previous) => previous,
  });

export const useProjects = (orgId: string) =>
  useQuery({ queryKey: keys.projects(orgId), queryFn: () => request<Project[]>(`/orgs/${orgId}/projects`) });

export const useProject = (orgId: string, id: string) =>
  useQuery({ queryKey: keys.project(orgId, id), queryFn: () => request<Project>(`/orgs/${orgId}/projects/${id}`) });

export const useCredentials = (orgId: string) =>
  useQuery({ queryKey: keys.credentials(orgId), queryFn: () => request<Credential[]>(`/orgs/${orgId}/credentials`) });

export const useCredentialApps = (orgId: string, id: string | null) =>
  useQuery({
    queryKey: keys.credentialApps(orgId, id ?? ""),
    queryFn: () => request<StoreApp[]>(`/orgs/${orgId}/credentials/${id}/apps`),
    enabled: !!id,
    retry: false,
  });

export const useMembers = (orgId: string) =>
  useQuery({ queryKey: keys.members(orgId), queryFn: () => request<Member[]>(`/orgs/${orgId}/members`) });

export const useInvitations = (orgId: string, enabled: boolean) =>
  useQuery({ queryKey: keys.invitations(orgId), queryFn: () => request<Invitation[]>(`/orgs/${orgId}/invitations`), enabled });

/**
 * Una mutazione che, riuscita, invalida le query indicate. Copre quasi tutte
 * le scritture del pannello: si manda, e le liste si ricaricano.
 */
export function useApiMutation<TBody = void, TResult = unknown>(
  build: (body: TBody) => { path: string; method?: string; body?: unknown },
  invalidate: readonly (readonly unknown[])[],
) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: TBody) => {
      const r = build(body);
      return request<TResult>(r.path, { method: r.method ?? "POST", body: r.body });
    },
    onSuccess: () => Promise.all(invalidate.map((queryKey) => client.invalidateQueries({ queryKey }))),
  });
}

export const roleRank: Record<OrgRole, number> = { Viewer: 0, Admin: 1, Owner: 2 };
export const canAdmin = (role: OrgRole) => roleRank[role] >= roleRank.Admin;
