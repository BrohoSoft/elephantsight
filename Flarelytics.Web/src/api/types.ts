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

/** Cosa vede un membro oltre al ruolo: tutti i progetti o alcuni, e le sezioni (MemberAccess in C#). */
export interface MemberAccess {
  allProjects: boolean;
  projectIds: string[];
  store: boolean;
  social: boolean;
}

export interface OrgSummary {
  id: string;
  name: string;
  role: OrgRole;
  access: MemberAccess;
}

export interface Me {
  id: string;
  email: string;
  fullName: string;
  twoFactorEnabled: boolean;
  /** Amministra l'installazione (SMTP, app social): vede le Impostazioni dell'istanza. */
  isInstanceAdmin: boolean;
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
  access: MemberAccess;
  joinedAtUtc: string;
}

export interface Invitation {
  id: string;
  email: string;
  role: OrgRole;
  access: MemberAccess;
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
  /** C'è un'app Meta configurata: si collegano Instagram e le Pagine Facebook. */
  metaEnabled: boolean;
  /** Da registrare nell'app Meta come URI di reindirizzamento OAuth. */
  metaRedirectUri: string;
  /** C'è un'app Instagram: si collegano account Instagram senza Pagina Facebook. */
  instagramEnabled: boolean;
  /** Da registrare nelle impostazioni di Business login di Instagram. */
  instagramRedirectUri: string;
  tikTokEnabled: boolean;
  /** Da registrare nell'app TikTok (Login Kit → Redirect URI). */
  tikTokRedirectUri: string;
  /** C'è un'app con il caso d'uso Threads: si collegano account Threads. */
  threadsEnabled: boolean;
  /** Da registrare nel caso d'uso Threads (Redirect Callback URLs). */
  threadsRedirectUri: string;
  /** Chi gestisce l'installazione e l'email di contatto, per /privacy e /terms. */
  legalOwner: string | null;
  legalContactEmail: string | null;
  version: string;
  /** Dove vanno immagini e video dei post: "unavailable" = modalità remota imposta e Bunny non configurato, niente caricamenti. */
  mediaStorage: "local" | "remote" | "unavailable";
  /** La modalità remota è imposta dall'ambiente (MEDIA_STORAGE=remote). */
  mediaStorageForced: boolean;
}

export interface CreatedInvitation {
  id: string;
  email: string;
  role: OrgRole;
  access: MemberAccess;
  expiresAtUtc: string;
  link: string;
  emailSent: boolean;
}

export type ReleaseStage = "Live" | "Rolling" | "InReview" | "Draft" | "Rejected" | "Retired" | "Processing" | "Other";

export interface VersionInfo {
  version: string;
  stage: ReleaseStage;
  rawState: string;
  track: string | null;
  createdAtUtc: string | null;
  rolloutPercent: number | null;
  buildNumbers: string[];
  releaseNotes: string | null;
}

export interface BuildInfo {
  version: string | null;
  buildNumber: string;
  stage: ReleaseStage;
  rawState: string;
  uploadedAtUtc: string | null;
  tracks: string[];
}

export interface StoreReleases {
  store: Store;
  appId: string;
  versions: VersionInfo[];
  builds: BuildInfo[];
  error: string | null;
}

export interface ReviewItem {
  id: string;
  store: Store;
  appId: string;
  projectId: string | null;
  projectName: string | null;
  iconUrl: string | null;
  rating: number;
  title: string | null;
  body: string;
  author: string | null;
  locale: string | null;
  appVersion: string | null;
  writtenAtUtc: string;
  replyText: string | null;
  repliedAtUtc: string | null;
  replyState: string | null;
}

export interface RatingSummary {
  store: Store;
  count: number;
  average: number;
  distribution: number[];
}

export interface ReviewPage {
  items: ReviewItem[];
  total: number;
  page: number;
  pageSize: number;
  summary: RatingSummary[];
  sync: { store: Store; appId: string; lastSyncedAtUtc: string | null; lastError: string | null }[];
}

export interface AppleLocaleText {
  locale: string;
  name: string | null;
  subtitle: string | null;
  privacyPolicyUrl: string | null;
  description: string | null;
  keywords: string | null;
  whatsNew: string | null;
  promotionalText: string | null;
  marketingUrl: string | null;
  supportUrl: string | null;
}

export interface AppleListing {
  version: string | null;
  infoEditable: boolean;
  versionEditable: boolean;
  locales: AppleLocaleText[];
}

export interface GoogleLocaleText {
  language: string;
  title: string;
  shortDescription: string;
  fullDescription: string;
  video: string | null;
}

export interface GoogleListing {
  defaultLanguage: string | null;
  locales: GoogleLocaleText[];
}

export interface StoreResult<T> {
  data: T | null;
  error: string | null;
}

export interface ListingResponse {
  appStore: StoreResult<AppleListing> | null;
  googlePlay: StoreResult<GoogleListing> | null;
}

export interface ImageGroup {
  group: string;
  images: { id: string; url: string; fileName: string | null }[];
}

export type SecretPlatform = "Android" | "Ios" | "Common";
export type SecretKind =
  | "AndroidKeystore" | "KeyProperties" | "GoogleServicesJson" | "IosCertificate"
  | "ProvisioningProfile" | "GoogleServiceInfoPlist" | "Environment" | "Other";

export interface SecretFile {
  id: string;
  platform: SecretPlatform;
  kind: SecretKind;
  name: string;
  fileName: string;
  sizeBytes: number;
  sha256: string;
  notes: string | null;
  createdAtUtc: string;
  lastDownloadedAtUtc: string | null;
}

export type BuildUploadStatus = "Queued" | "Uploading" | "Processing" | "Completed" | "Failed";

export interface BuildUploadItem {
  id: string;
  store: Store;
  fileName: string;
  sizeBytes: number;
  version: string | null;
  buildNumber: string | null;
  track: string | null;
  releaseStatus: string | null;
  rolloutPercent: number | null;
  status: BuildUploadStatus;
  message: string | null;
  createdAtUtc: string;
  finishedAtUtc: string | null;
}

export type SocialNetwork = "Bluesky" | "Mastodon" | "Instagram" | "FacebookPage" | "TikTok" | "Threads";
export type SocialAccountStatus = "Connected" | "NeedsReconnect";

/** I limiti di una rete, gli stessi che il server ricontrolla (SocialRules). */
export interface NetworkLimits {
  maxCharacters: number;
  maxImages: number;
  requiresMedia: boolean;
  maxImageBytes: number;
  minAspectRatio: number | null;
  maxAspectRatio: number | null;
  maxHashtags: number | null;
  characterCounting: "graphemes" | "codepoints" | "mastodon";
  /** none: niente video; optional: Instagram, diventa un Reel; required: TikTok. */
  video: "none" | "optional" | "required";
  maxVideoBytes: number;
  minVideoSeconds: number;
  maxVideoSeconds: number;
  /** Il video deve avere l'indice in testa (Reel di Instagram). */
  requiresFastStart: boolean;
}

export interface SocialAccount {
  id: string;
  network: SocialNetwork;
  name: string;
  handle: string | null;
  serverUrl: string | null;
  status: SocialAccountStatus;
  statusMessage: string | null;
  limits: NetworkLimits;
  /** I progetti in cui l'account si usa (solo quelli che il membro vede). */
  projectIds: string[];
  createdAtUtc: string;
}

export type MediaKind = "Image" | "Video";

export interface SocialMediaItem {
  id: string;
  kind: MediaKind;
  durationMs: number | null;
  fastStart: boolean;
  width: number;
  height: number;
  sizeBytes: number;
  altText: string | null;
  /** L'originale, firmato e a scadenza: si usa così com'è in un <img>. Null se è stato cancellato dopo la pubblicazione. */
  url: string | null;
  /** La miniatura (~400 px), firmata: resta anche quando l'originale non c'è più. */
  thumbnailUrl: string | null;
  /** L'originale è stato cancellato qualche giorno dopo la pubblicazione: restano la miniatura e il link del post. */
  originalDeleted: boolean;
}

export type SocialTargetStatus = "Pending" | "Publishing" | "Published" | "Failed";
export type SocialPostStatus = "Draft" | "Scheduled" | "Publishing" | "Published" | "PartiallyFailed" | "Failed" | "Inbox";

export interface SocialTarget {
  id: string;
  accountId: string | null;
  network: SocialNetwork;
  accountName: string;
  textOverride: string | null;
  status: SocialTargetStatus;
  externalUrl: string | null;
  error: string | null;
  nextAttemptAtUtc: string | null;
  publishedAtUtc: string | null;
}

export interface SocialPost {
  id: string;
  text: string;
  scheduledAtUtc: string;
  isDraft: boolean;
  projectId: string | null;
  status: SocialPostStatus;
  editable: boolean;
  /** Pubblicato fuori da ElephantSight (Business Suite, l'app…) e copiato qui: si legge e basta. */
  imported: boolean;
  /** Arrivato con una chiave API, in attesa nella coda "Da programmare". */
  inbox: boolean;
  /** La data proposta da chi l'ha mandato. */
  suggestedAtUtc: string | null;
  externalRef: string | null;
  options: PostOptions;
  /** Il nome della chiave API da cui è arrivato (solo nella coda). */
  source?: string | null;
  media: SocialMediaItem[];
  targets: SocialTarget[];
  createdAtUtc: string;
  /** Un'uscita di questo post ricorrente. */
  recurringPostId: string | null;
}

export type RecurrenceFrequency = "Daily" | "Weekly" | "Monthly";
export type Weekday = "Sunday" | "Monday" | "Tuesday" | "Wednesday" | "Thursday" | "Friday" | "Saturday";

/** Un post ricorrente (SocialRecurringPost): contenuto, account e regola. */
export interface RecurringPost {
  id: string;
  text: string;
  projectId: string | null;
  accountIds: string[];
  options: PostOptions;
  media: SocialMediaItem[];
  frequency: RecurrenceFrequency;
  interval: number;
  daysOfWeek: Weekday[];
  /** "HH:mm" nel fuso timeZone. */
  timeOfDay: string;
  timeZone: string;
  /** "yyyy-MM-dd": il primo giorno, e per il mensile il giorno del mese. */
  startDate: string;
  endDate: string | null;
  isPaused: boolean;
  nextOccurrenceUtc: string | null;
  /** Le prossime uscite (vuoto se in pausa o finito). */
  upcoming: string[];
  occurrenceCount: number;
  /** L'ultima uscita, con il suo esito (solo nell'elenco). */
  lastPost: SocialPost | null;
  createdAtUtc: string;
}

/** Un'uscita futura di un post ricorrente, per il calendario. */
export interface RecurringOccurrence {
  recurringPostId: string;
  atUtc: string;
}

export interface MetaCandidate {
  key: string;
  network: SocialNetwork;
  name: string;
  handle: string | null;
  alreadyConnected: boolean;
}

export interface MetaCandidates {
  candidates: MetaCandidate[];
  selection: string;
}

export interface ApiKeyItem {
  id: string;
  name: string;
  prefix: string;
  createdBy: string | null;
  createdAtUtc: string;
  lastUsedAtUtc: string | null;
}

export interface CreatedApiKey {
  key: ApiKeyItem;
  /** La chiave da copiare: non si potrà più rileggere. */
  secret: string;
}

export interface AssignResult {
  postId: string;
  scheduled: boolean;
  problem: string | null;
}

export type TikTokPrivacy = "PUBLIC_TO_EVERYONE" | "MUTUAL_FOLLOW_FRIENDS" | "FOLLOWER_OF_CREATOR" | "SELF_ONLY";

/** Le scelte del post che valgono per alcune reti (vedi PostOptions in C#). */
export interface PostOptions {
  instagramShowInGrid: boolean;
  tikTokPrivacy: TikTokPrivacy | null;
  tikTokAllowComment: boolean;
  tikTokAllowDuet: boolean;
  tikTokAllowStitch: boolean;
  tikTokBrandOrganic: boolean;
  tikTokBrandedContent: boolean;
}

export const defaultPostOptions: PostOptions = {
  instagramShowInGrid: true,
  tikTokPrivacy: null,
  tikTokAllowComment: false,
  tikTokAllowDuet: false,
  tikTokAllowStitch: false,
  tikTokBrandOrganic: false,
  tikTokBrandedContent: false,
};

/** Quello che TikTok dice del creator: va mostrato mentre si prepara il post. */
export interface TikTokCreator {
  username: string;
  nickname: string;
  privacyLevels: TikTokPrivacy[];
  commentDisabled: boolean;
  duetDisabled: boolean;
  stitchDisabled: boolean;
  maxVideoSeconds: number;
}

export type LogLevelName = "Trace" | "Debug" | "Information" | "Warning" | "Error" | "Critical";

/** Un messaggio del log salvato a database (pagina Log). */
export interface LogItem {
  id: number;
  timestampUtc: string;
  level: LogLevelName;
  /** Social, Store o Sistema. */
  area: string;
  category: string;
  message: string;
  exception: string | null;
  /** Di sistema, di nessuna organizzazione (li vede solo l'owner). */
  system: boolean;
}

export interface LogPage {
  items: LogItem[];
  hasMore: boolean;
}

/** Un campo delle impostazioni dell'istanza: i segreti non tornano mai (value null). */
export interface SettingValue {
  name: string;
  secret: boolean;
  value: string | null;
  set: boolean;
  /** panel = impostato dal pannello, env = dal file .env, null = non impostato. */
  source: "panel" | "env" | null;
}

export interface SettingsGroup {
  group: "smtp" | "meta" | "instagram" | "tiktok" | "threads" | "bunny";
  fields: SettingValue[];
}

export interface InstanceAdminItem {
  userId: string;
  email: string;
  fullName: string;
}

/** Un post in arrivo nella panoramica: programmato (postId) o uscita di un post ricorrente (recurringPostId). */
export interface UpcomingItem {
  atUtc: string;
  text: string;
  networks: SocialNetwork[];
  postId: string | null;
  recurringPostId: string | null;
  projectId: string | null;
}

export interface SocialOverview {
  scheduled: number;
  inbox: number;
  recurringActive: number;
  failedLastWeek: number;
  upcoming: UpcomingItem[];
}

/** La panoramica (GET /overview): ogni parte c'è solo se il membro la può vedere. */
export interface Overview {
  projects: number;
  social: SocialOverview | null;
  logs: LogItem[] | null;
}
