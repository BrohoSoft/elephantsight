import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { useNavigate, useSearchParams } from "react-router";
import { errorMessage, request } from "../../api/client";
import { keys, useMe } from "../../api/hooks";
import type { MetaCandidates } from "../../api/types";
import { lastOrg } from "../../components/AppShell";
import { NetworkGlyph } from "../../components/SocialIcons";
import { Alert, Badge, Button, PageHeader, PageLoader, Panel } from "../../components/ui";
import { networkName } from "../../social/rules";
import { oauthOrgKey } from "./Accounts";

/** L'organizzazione da cui è partito il login: quella salvata prima di uscire verso Facebook o Instagram. */
function useOAuthOrg(): string | null {
  const me = useMe();
  let orgId: string | null = null;
  try {
    orgId = sessionStorage.getItem(oauthOrgKey);
  } catch {
    /* niente storage */
  }
  return orgId ?? lastOrg() ?? me.data?.organizations[0]?.id ?? null;
}

/**
 * Dove Instagram rimanda dopo il login. Un login è un account solo: l'API lo
 * collega subito, e si torna agli account.
 */
export function InstagramCallbackPage() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const orgId = useOAuthOrg();
  const [error, setError] = useState<unknown>(null);
  const started = useRef(false);

  const code = params.get("code");
  const state = params.get("state");
  const denied = params.get("error_description") ?? (params.get("error") ? "Accesso negato su Instagram." : null);

  useEffect(() => {
    // Una volta sola: il codice vale per un solo scambio.
    if (started.current || !code || !state || !orgId) return;
    started.current = true;
    request(`/orgs/${orgId}/social/instagram/complete`, { method: "POST", body: { code, state } })
      .then(() => queryClient.invalidateQueries({ queryKey: keys.socialAccounts(orgId) }))
      .then(() => navigate(`/o/${orgId}/social/accounts`, { replace: true }))
      .catch(setError);
  }, [code, state, orgId, navigate, queryClient]);

  if (!denied && code && state && !error) return <PageLoader />;

  return (
    <div className="mx-auto max-w-lg space-y-4">
      <PageHeader title="Collegamento con Instagram" />
      <Alert tone="bad">{error ? errorMessage(error) : denied ?? "Instagram non ha restituito il codice di accesso."}</Alert>
      <Button onClick={() => navigate(orgId ? `/o/${orgId}/social/accounts` : "/", { replace: true })}>Torna agli account</Button>
    </div>
  );
}

/**
 * Dove Facebook rimanda dopo il login. Qui il codice passa all'API con la
 * sessione dell'utente; l'API restituisce Pagine e account Instagram, e si
 * sceglie quali collegare.
 */
export function MetaCallbackPage() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [result, setResult] = useState<MetaCandidates | null>(null);
  const [chosen, setChosen] = useState<string[]>([]);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const started = useRef(false);

  const orgId = useOAuthOrg();

  const code = params.get("code");
  const state = params.get("state");
  const denied = params.get("error_description") ?? (params.get("error") ? "Accesso negato su Facebook." : null);

  useEffect(() => {
    // Una volta sola: il codice di Facebook vale per un solo scambio, e in
    // sviluppo React esegue gli effetti due volte.
    if (started.current || !code || !state || !orgId) return;
    started.current = true;
    request<MetaCandidates>(`/orgs/${orgId}/social/meta/complete`, { method: "POST", body: { code, state } })
      .then((r) => {
        setResult(r);
        setChosen(r.candidates.map((c) => c.key));
      })
      .catch(setError);
  }, [code, state, orgId]);

  async function connect() {
    setBusy(true);
    setError(null);
    try {
      await request(`/orgs/${orgId}/social/meta/accounts`, { method: "POST", body: { selection: result!.selection, keys: chosen } });
      await queryClient.invalidateQueries({ queryKey: keys.socialAccounts(orgId!) });
      navigate(`/o/${orgId}/social/accounts`, { replace: true });
    } catch (e) {
      setError(e);
      setBusy(false);
    }
  }

  const back = () => navigate(orgId ? `/o/${orgId}/social/accounts` : "/", { replace: true });

  if (denied || !code || !state) {
    return (
      <div className="mx-auto max-w-lg space-y-4">
        <PageHeader title="Collegamento con Facebook" />
        <Alert tone="bad">{denied ?? "Facebook non ha restituito il codice di accesso."}</Alert>
        <Button onClick={back}>Torna agli account</Button>
      </div>
    );
  }

  if (!result && !error) return <PageLoader />;

  return (
    <div className="mx-auto max-w-lg">
      <PageHeader title="Scegli gli account" description="Le Pagine e gli account Instagram che hai autorizzato su Facebook." />
      {error ? (
        <div className="space-y-4">
          <Alert tone="bad">{errorMessage(error)}</Alert>
          <Button onClick={back}>Torna agli account</Button>
        </div>
      ) : result!.candidates.length === 0 ? (
        <div className="space-y-4">
          <Alert tone="warn" title="Nessuna Pagina">
            Facebook non ha dato accesso a nessuna Pagina. Riprova e, nella finestra di Facebook, scegli le Pagine (e gli account Instagram collegati) da autorizzare.
          </Alert>
          <Button onClick={back}>Torna agli account</Button>
        </div>
      ) : (
        <Panel footer={<><Button variant="ghost" onClick={back}>Annulla</Button><Button variant="primary" loading={busy} disabled={chosen.length === 0} onClick={connect}>Collega {chosen.length}</Button></>}>
          <ul className="divide-y divide-line">
            {result!.candidates.map((c) => (
              <li key={c.key}>
                <label className="flex cursor-pointer items-center gap-3 px-4 py-3">
                  <input type="checkbox" className="accent-brand" checked={chosen.includes(c.key)}
                    onChange={() => setChosen((s) => (s.includes(c.key) ? s.filter((k) => k !== c.key) : [...s, c.key]))} />
                  <NetworkGlyph network={c.network} className="text-muted" />
                  <span className="min-w-0 flex-1 text-[13px] text-fg">
                    {c.handle ?? c.name} <span className="text-xs text-muted">· {networkName[c.network]}</span>
                  </span>
                  {c.alreadyConnected && <Badge>Già collegato: si aggiorna</Badge>}
                </label>
              </li>
            ))}
          </ul>
        </Panel>
      )}
    </div>
  );
}
