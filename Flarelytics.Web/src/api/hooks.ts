import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { request } from "./client";
import type {
  Metrics,
  Credential,
  InstanceInfo,
  BuildUploadItem,
  ImageGroup,
  ListingResponse,
  ReviewPage,
  SecretFile,
  SocialAccount,
  SocialPost,
  RecurringOccurrence,
  RecurringPost,
  ApiKeyItem,
  Store,
  StoreReleases,
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
  releases: (orgId: string, projectId: string) => ["org", orgId, "projects", projectId, "releases"] as const,
  reviews: (orgId: string) => ["org", orgId, "reviews"] as const,
  listing: (orgId: string, projectId: string) => ["org", orgId, "projects", projectId, "listing"] as const,
  screenshots: (orgId: string, projectId: string, store: Store, locale: string) => ["org", orgId, "projects", projectId, "listing", store, locale] as const,
  files: (orgId: string, projectId: string) => ["org", orgId, "projects", projectId, "files"] as const,
  uploads: (orgId: string, projectId: string) => ["org", orgId, "projects", projectId, "uploads"] as const,
  socialAccounts: (orgId: string) => ["org", orgId, "social", "accounts"] as const,
  socialPosts: (orgId: string) => ["org", orgId, "social", "posts"] as const,
  socialInbox: (orgId: string) => ["org", orgId, "social", "inbox"] as const,
  socialRecurring: (orgId: string) => ["org", orgId, "social", "recurring"] as const,
  apiKeys: (orgId: string) => ["org", orgId, "api-keys"] as const,
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

export const useReleases = (orgId: string, projectId: string) =>
  useQuery({ queryKey: keys.releases(orgId, projectId), queryFn: () => request<StoreReleases[]>(`/orgs/${orgId}/projects/${projectId}/releases`), staleTime: 60_000 });

export interface ReviewFilters {
  projectId?: string;
  store?: Store;
  rating?: number;
  unanswered?: boolean;
  page?: number;
}

export function useReviews(orgId: string, filters: ReviewFilters) {
  const query = new URLSearchParams();
  Object.entries(filters).forEach(([k, v]) => v !== undefined && v !== false && query.set(k, String(v)));
  return useQuery({
    queryKey: [...keys.reviews(orgId), filters],
    queryFn: () => request<ReviewPage>(`/orgs/${orgId}/reviews?${query}`),
    placeholderData: (previous) => previous,
  });
}

export const useListing = (orgId: string, projectId: string) =>
  useQuery({ queryKey: keys.listing(orgId, projectId), queryFn: () => request<ListingResponse>(`/orgs/${orgId}/projects/${projectId}/listing`), staleTime: 60_000 });

export const useScreenshots = (orgId: string, projectId: string, store: Store, locale: string | null) =>
  useQuery({
    queryKey: keys.screenshots(orgId, projectId, store, locale ?? ""),
    queryFn: () => request<ImageGroup[]>(`/orgs/${orgId}/projects/${projectId}/listing/screenshots?store=${store}&locale=${encodeURIComponent(locale!)}`),
    enabled: !!locale,
    staleTime: 60_000,
  });

export const useSecretFiles = (orgId: string, projectId: string) =>
  useQuery({ queryKey: keys.files(orgId, projectId), queryFn: () => request<SecretFile[]>(`/orgs/${orgId}/projects/${projectId}/files`) });

/** Si aggiorna da sola finché c'è un caricamento in corso: lo stato lo cambia il worker. */
export const useBuildUploads = (orgId: string, projectId: string) =>
  useQuery({
    queryKey: keys.uploads(orgId, projectId),
    queryFn: () => request<BuildUploadItem[]>(`/orgs/${orgId}/projects/${projectId}/builds/uploads`),
    refetchInterval: (q) => (q.state.data?.some((u) => u.status === "Queued" || u.status === "Uploading" || u.status === "Processing") ? 5000 : false),
  });

export const useSocialAccounts = (orgId: string) =>
  useQuery({ queryKey: keys.socialAccounts(orgId), queryFn: () => request<SocialAccount[]>(`/orgs/${orgId}/social/accounts`) });

/**
 * I post fra due istanti. Si aggiorna da sola finché c'è qualcosa in uscita
 * (in pubblicazione, o programmato per un momento già passato): lo stato lo
 * cambia il worker.
 */
export const useSocialPosts = (orgId: string, from: Date, to: Date, projectId?: string) =>
  useQuery({
    queryKey: [...keys.socialPosts(orgId), from.toISOString(), to.toISOString(), projectId ?? "all"],
    queryFn: () =>
      request<SocialPost[]>(
        `/orgs/${orgId}/social/posts?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}${projectId ? `&projectId=${projectId}` : ""}`,
      ),
    placeholderData: (previous) => previous,
    refetchInterval: (q) =>
      q.state.data?.some((p) => p.status === "Publishing" || (p.status === "Scheduled" && Date.parse(p.scheduledAtUtc) <= Date.now() + 60_000)) ? 10_000 : false,
  });

/** La coda "Da programmare": si ricarica ogni minuto, i post arrivano dall'esterno. */
export const useSocialInbox = (orgId: string) =>
  useQuery({ queryKey: keys.socialInbox(orgId), queryFn: () => request<SocialPost[]>(`/orgs/${orgId}/social/inbox`), refetchInterval: 60_000 });

/** I post ricorrenti che il membro vede, o (con projectId) quelli di un progetto. */
export const useSocialRecurring = (orgId: string, projectId?: string) =>
  useQuery({
    queryKey: [...keys.socialRecurring(orgId), projectId ?? "all"],
    queryFn: () => request<RecurringPost[]>(`/orgs/${orgId}/social/recurring${projectId ? `?projectId=${projectId}` : ""}`),
  });

/** Le uscite future dei post ricorrenti nel periodo del calendario: calcolate dal server con la regola. */
export const useRecurringOccurrences = (orgId: string, from: Date, to: Date, projectId?: string) =>
  useQuery({
    queryKey: [...keys.socialRecurring(orgId), "occurrences", from.toISOString(), to.toISOString(), projectId ?? "all"],
    queryFn: () =>
      request<RecurringOccurrence[]>(
        `/orgs/${orgId}/social/recurring/occurrences?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}${projectId ? `&projectId=${projectId}` : ""}`,
      ),
    placeholderData: (previous) => previous,
  });

export const useApiKeys = (orgId: string, enabled: boolean) =>
  useQuery({ queryKey: keys.apiKeys(orgId), queryFn: () => request<ApiKeyItem[]>(`/orgs/${orgId}/api-keys`), enabled });
