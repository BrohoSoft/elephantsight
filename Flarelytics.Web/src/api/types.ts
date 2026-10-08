// Le forme delle risposte dell'API. Tenute a mano e allineate ai record C#:
// sono poche, e un generatore aggiungerebbe un passo di build per risparmiare
// poco.

export type Store = "AppStore" | "GooglePlay";
export type OrgRole = "Viewer" | "Admin" | "Owner";
export type CredentialStatus = "Valid" | "Limited" | "Invalid";

export interface SessionUser {
  id: string;
  email: string;
  fullName: string;
}

export interface Session {
  accessToken: string;
  accessTokenExpiresAtUtc: string;
  user: SessionUser;
}

export interface SecondFactorChallenge {
  challengeToken: string;
  twoFactorRequired: true;
}

export interface OrgSummary {
  id: string;
  name: string;
  role: OrgRole;
}

export interface Me {
  id: string;
  email: string;
  fullName: string;
  twoFactorEnabled: boolean;
  organizations: OrgSummary[];
}

export interface ProjectApp {
  id: string;
  store: Store;
  externalAppId: string;
  displayName: string | null;
  credentialId: string;
  credentialLabel: string;
  iconUrl: string | null;
}

export interface Project {
  id: string;
  name: string;
  description: string | null;
  apps: ProjectApp[];
  createdAtUtc: string;
  /** L'icona dell'App Store se c'è, altrimenti quella di Google Play. */
  iconUrl: string | null;
}

export interface Credential {
  id: string;
  store: Store;
  label: string;
  keyId: string | null;
  issuerId: string | null;
  vendorNumber: string | null;
  clientEmail: string | null;
  reportsBucket: string | null;
  status: CredentialStatus;
  statusMessage: string | null;
  lastVerifiedAtUtc: string | null;
  createdAtUtc: string;
  lastCheckMessage?: string | null;
  syncRequested: boolean;
  lastSyncCompletedAtUtc: string | null;
  lastSyncError: string | null;
  daysImported: number;
  latestReportDate: string | null;
}

export interface StoreApp {
  externalId: string;
  name: string;
  bundleId: string;
}

export interface Member {
  userId: string;
  email: string;
  fullName: string;
  role: OrgRole;
  joinedAtUtc: string;
}

export interface Invitation {
  id: string;
  email: string;
  role: OrgRole;
  createdAtUtc: string;
  expiresAtUtc: string;
}

export interface InvitationPreview {
  organizationName: string;
  email: string;
  role: OrgRole;
  invitedBy: string;
  accountExists: boolean;
}

export interface StoreTotals {
  store: Store;
  downloads: number;
  redownloads: number;
  updates: number;
  uninstalls: number;
  inAppPurchases: number;
  refunds: number;
  proceedsEur: number;
  salesEur: number;
}

export interface Metrics {
  from: string | null;
  to: string | null;
  days: number;
  currency: string;
  hasLinkedApps: boolean;
  byStore: StoreTotals[];
  previousDownloads: number;
  previousProceedsEur: number;
  daily: { date: string; store: Store; downloads: number; proceedsEur: number }[];
  countries: { countryCode: string; downloads: number; proceedsEur: number }[];
  byProject: { projectId: string; downloads: number; proceedsEur: number }[];
  hasUnconvertedAmounts: boolean;
  lastSyncAtUtc: string | null;
  /** Quali metriche fornisce ogni store: dove manca si mostra un trattino, non uno zero. */
  coverage: { store: Store; metrics: string[]; proceedsThrough: string | null }[];
}

export interface InstanceInfo {
  setupRequired: boolean;
  emailEnabled: boolean;
  version: string;
}

export interface CreatedInvitation {
  id: string;
  email: string;
  role: OrgRole;
  expiresAtUtc: string;
  link: string;
  emailSent: boolean;
}
