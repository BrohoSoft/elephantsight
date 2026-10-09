import { Building2 } from "lucide-react";
import type { ReactNode } from "react";
import { Navigate, Route, Routes, useLocation } from "react-router";
import { useInstance, useMe } from "./api/hooks";
import { useAuth } from "./auth/AuthContext";
import { AppShell, lastOrg } from "./components/AppShell";
import { EmptyState, PageLoader } from "./components/ui";
import { canManageOrg, hasSocial, hasStore, homePath, isFull, useOrg } from "./components/org";
import type { OrgSummary } from "./api/types";
import { AccountPage } from "./pages/Account";
import { useTheme } from "./theme";
import { AcceptInvitePage } from "./pages/auth/AcceptInvite";
import { ForgotPasswordPage, ResetPasswordPage } from "./pages/auth/EmailLinks";
import { SetupPage } from "./pages/auth/Setup";
import { LoginPage } from "./pages/auth/Login";
import { RegisterPage } from "./pages/auth/Register";
import { CredentialsPage } from "./pages/Credentials";
import { MembersPage } from "./pages/Members";
import { OrgSettingsPage } from "./pages/OrgSettings";
import { OverviewPage } from "./pages/Overview";
import { ProjectDetailPage } from "./pages/ProjectDetail";
import { ProjectsPage } from "./pages/Projects";
import { ReviewsPage } from "./pages/Reviews";
import { SocialAccountsPage } from "./pages/social/Accounts";
import { SocialCalendarPage } from "./pages/social/Calendar";
import { SocialInboxPage } from "./pages/social/Inbox";
import { SocialRecurringPage } from "./pages/social/Recurring";
import { ApiKeysPage } from "./pages/ApiKeys";
import { PrivacyPage, TermsPage } from "./pages/Legal";
import { MetaCallbackPage, SingleAccountCallbackPage } from "./pages/social/OAuthCallback";

/** Le pagine interne: chi non ha una sessione va all'accesso, e poi torna qui. */
function RequireAuth({ children }: { children: ReactNode }) {
  const { status } = useAuth();
  const location = useLocation();

  if (status === "loading") return <PageLoader />;
  if (status === "anonymous") {
    return <Navigate to={`/login?next=${encodeURIComponent(location.pathname + location.search)}`} replace />;
  }
  return <>{children}</>;
}

/** Login e registrazione: chi è già dentro va al pannello. */
function AnonymousOnly({ children }: { children: ReactNode }) {
  const { status } = useAuth();
  if (status === "loading") return <PageLoader />;
  if (status === "authenticated") return <Navigate to="/" replace />;
  return <>{children}</>;
}

/** La radice porta alla prima organizzazione. */
function Home() {
  const me = useMe();
  if (me.isPending) return <PageLoader />;

  const orgs = me.data?.organizations ?? [];
  const first = orgs.find((o) => o.id === lastOrg()) ?? orgs[0];
  if (first) return <Navigate to={`/o/${first.id}`} replace />;

  return (
    <EmptyState icon={<Building2 className="size-5" />} title="Non fai parte di nessuna organizzazione">
      Creane una dal selettore in alto a sinistra, o chiedi a un collega di invitarti.
    </EmptyState>
  );
}

/** Un id di organizzazione che non è tra le proprie: la shell non trova l'organizzazione e si torna alla radice. */
function OrgGuard({ children }: { children: ReactNode }) {
  const me = useMe();
  const location = useLocation();
  const orgId = location.pathname.split("/")[2];

  if (me.isPending) return <PageLoader />;
  if (!me.data?.organizations.some((o) => o.id === orgId)) return <Navigate to="/" replace />;
  return <>{children}</>;
}

/**
 * Una pagina che il membro non vede (sezione spenta, o gestione
 * dell'organizzazione senza accesso completo): si va alla sua prima pagina.
 * Il server rifiuterebbe comunque; così non si apre una pagina piena di errori.
 */
function Allowed({ when, children }: { when: (org: OrgSummary) => boolean; children: ReactNode }) {
  const org = useOrg();
  if (!when(org)) return <Navigate to={homePath(org)} replace />;
  return <>{children}</>;
}

export function App() {
  // Sempre montato: con la preferenza "Sistema" il tema deve seguire il
  // sistema operativo anche quando nessun selettore è a schermo.
  useTheme();
  const instance = useInstance();
  const location = useLocation();

  if (instance.isPending) return <PageLoader />;

  // Un'istanza appena installata non ha utenti: tutto porta all'installer,
  // e l'installer non si riapre dopo.
  const setupRequired = instance.data?.setupRequired ?? false;
  // Privacy e termini restano leggibili sempre: li aprono i revisori di TikTok e Meta.
  const isLegal = location.pathname === "/privacy" || location.pathname === "/terms";
  if (setupRequired && location.pathname !== "/setup" && !isLegal) return <Navigate to="/setup" replace />;

  return (
    <Routes>
      <Route path="/privacy" element={<PrivacyPage />} />
      <Route path="/terms" element={<TermsPage />} />
      <Route path="/setup" element={setupRequired ? <SetupPage /> : <Navigate to="/" replace />} />
      <Route path="/login" element={<AnonymousOnly><LoginPage /></AnonymousOnly>} />
      <Route path="/register" element={<AnonymousOnly><RegisterPage /></AnonymousOnly>} />
      <Route path="/forgot-password" element={<ForgotPasswordPage />} />
      <Route path="/reset-password" element={<ResetPasswordPage />} />
      <Route path="/accept-invite" element={<AcceptInvitePage />} />

      <Route element={<RequireAuth><AppShell /></RequireAuth>}>
        <Route index element={<Home />} />
        <Route path="/account" element={<AccountPage />} />
        <Route path="/o/:orgId" element={<OrgGuard><Allowed when={hasStore}><OverviewPage /></Allowed></OrgGuard>} />
        <Route path="/o/:orgId/projects" element={<OrgGuard><ProjectsPage /></OrgGuard>} />
        <Route path="/o/:orgId/projects/:projectId" element={<OrgGuard><ProjectDetailPage /></OrgGuard>} />
        <Route path="/o/:orgId/projects/:projectId/:tab" element={<OrgGuard><ProjectDetailPage /></OrgGuard>} />
        <Route path="/o/:orgId/reviews" element={<OrgGuard><Allowed when={hasStore}><ReviewsPage /></Allowed></OrgGuard>} />
        <Route path="/o/:orgId/credentials" element={<OrgGuard><Allowed when={(o) => hasStore(o) && isFull(o)}><CredentialsPage /></Allowed></OrgGuard>} />
        <Route path="/o/:orgId/social" element={<OrgGuard><Allowed when={hasSocial}><SocialCalendarPage /></Allowed></OrgGuard>} />
        <Route path="/o/:orgId/social/accounts" element={<OrgGuard><Allowed when={hasSocial}><SocialAccountsPage /></Allowed></OrgGuard>} />
        <Route path="/o/:orgId/social/inbox" element={<OrgGuard><Allowed when={hasSocial}><SocialInboxPage /></Allowed></OrgGuard>} />
        <Route path="/o/:orgId/social/recurring" element={<OrgGuard><Allowed when={hasSocial}><SocialRecurringPage /></Allowed></OrgGuard>} />
        <Route path="/o/:orgId/api-keys" element={<OrgGuard><Allowed when={canManageOrg}><ApiKeysPage /></Allowed></OrgGuard>} />
        {/* I ritorni dai login di Facebook, Instagram, TikTok e Threads: indirizzi fissi, registrati nelle app. */}
        <Route path="/social/meta/callback" element={<MetaCallbackPage />} />
        <Route path="/social/instagram/callback" element={<SingleAccountCallbackPage provider="instagram" />} />
        <Route path="/social/tiktok/callback" element={<SingleAccountCallbackPage provider="tiktok" />} />
        <Route path="/social/threads/callback" element={<SingleAccountCallbackPage provider="threads" />} />
        <Route path="/o/:orgId/members" element={<OrgGuard><Allowed when={canManageOrg}><MembersPage /></Allowed></OrgGuard>} />
        <Route path="/o/:orgId/settings" element={<OrgGuard><Allowed when={canManageOrg}><OrgSettingsPage /></Allowed></OrgGuard>} />
      </Route>

      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
