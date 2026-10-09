import { useQueryClient } from "@tanstack/react-query";
import clsx from "clsx";
import { useState } from "react";
import { errorMessage, request } from "../api/client";
import { keys } from "../api/hooks";
import { defaultPostOptions, type PostOptions, type SocialAccount, type SocialPost } from "../api/types";
import { formatDateTime, useOrg } from "../components/org";
import { NetworkGlyph } from "../components/SocialIcons";
import { Alert, Button, Panel, Segmented } from "../components/ui";
import { commercialIncomplete, NetworkOptions } from "./NetworkOptions";
import { accountLabel } from "./rules";

interface ChangeAccountsResult {
  postId: string;
  text: string;
  scheduledAtUtc: string;
  changed: boolean;
  problem: string | null;
}

/**
 * Aggiungere o togliere account a più post insieme: quelli selezionati nel
 * calendario, o tutti quelli ancora da uscire (anche nei mesi dopo). Ogni post
 * si controlla da solo: quelli che non vanno bene per il social scelto restano
 * com'erano, e qui sotto si vede perché.
 */
export function BulkAccounts({ selected, accounts, onDone }: { selected: SocialPost[]; accounts: SocialAccount[]; onDone: () => void }) {
  const org = useOrg();
  const queryClient = useQueryClient();
  const [scope, setScope] = useState<"selected" | "upcoming">("selected");
  const [mode, setMode] = useState<"add" | "remove">("add");
  const [chosen, setChosen] = useState<string[]>([]);
  const [options, setOptions] = useState<PostOptions>(defaultPostOptions);
  const [commercial, setCommercial] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [results, setResults] = useState<ChangeAccountsResult[] | null>(null);

  const chosenAccounts = accounts.filter((a) => chosen.includes(a.id));
  const addingTikTok = mode === "add" && chosenAccounts.some((a) => a.network === "TikTok");
  const tikTokIncomplete = addingTikTok && (!options.tikTokPrivacy || commercialIncomplete(options, commercial));
  const count = scope === "selected" ? selected.length : null;

  async function apply() {
    setBusy(true);
    setError(null);
    setResults(null);
    try {
      const response = await request<ChangeAccountsResult[]>(`/orgs/${org.id}/social/posts/accounts`, {
        method: "POST",
        body: {
          postIds: scope === "selected" ? selected.map((p) => p.id) : null,
          allUpcoming: scope === "upcoming",
          addAccountIds: mode === "add" ? chosen : [],
          removeAccountIds: mode === "remove" ? chosen : [],
          options: addingTikTok ? options : null,
        },
      });
      setResults(response);
      await queryClient.invalidateQueries({ queryKey: keys.socialPosts(org.id) });
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  }

  const changed = results?.filter((r) => r.changed).length ?? 0;
  const problems = results?.filter((r) => r.problem) ?? [];

  return (
    <Panel className="mb-4" title="Cambia gli account dei post"
      description="Il social scelto si aggiunge o si toglie da ogni post; sugli altri account il post resta com'era. I post già usciti non cambiano."
      actions={<Button size="sm" variant="ghost" onClick={onDone}>Chiudi</Button>}>
      <div className="space-y-4 p-4">
        <div className="flex flex-wrap gap-3">
          <Segmented value={scope} onChange={setScope} options={[
            { value: "selected", label: `${selected.length} selezionati nel calendario` },
            { value: "upcoming", label: "Tutti i post ancora da uscire" },
          ]} />
          <Segmented value={mode} onChange={(m) => { setMode(m); setResults(null); }} options={[
            { value: "add", label: "Aggiungi" },
            { value: "remove", label: "Togli" },
          ]} />
        </div>
        {scope === "selected" && selected.length === 0 && (
          <p className="text-[0.8125rem] text-muted">Clicca i post nel calendario per selezionarli, o scegli "Tutti i post ancora da uscire".</p>
        )}

        <div className="flex flex-wrap gap-1.5">
          {accounts.map((a) => {
            const on = chosen.includes(a.id);
            return (
              <button key={a.id} type="button" aria-pressed={on} disabled={mode === "add" && a.status === "NeedsReconnect"}
                onClick={() => setChosen((c) => (c.includes(a.id) ? c.filter((x) => x !== a.id) : [...c, a.id]))}
                className={clsx("inline-flex h-8 items-center gap-1.5 rounded-md border px-2.5 text-[0.8125rem] transition-colors disabled:opacity-50",
                  on ? (mode === "add" ? "border-brand/60 bg-brand/10 text-fg" : "border-bad/50 bg-bad/10 text-fg") : "border-line-strong bg-panel-2 text-muted hover:text-fg")}>
                <NetworkGlyph network={a.network} className="size-3.5" />{accountLabel(a)}
              </button>
            );
          })}
        </div>

        {addingTikTok && (
          <NetworkOptions accounts={chosenAccounts.filter((a) => a.network === "TikTok")} media={selected.flatMap((p) => p.media)}
            options={options} onChange={setOptions} commercial={commercial} onCommercial={setCommercial} readOnly={false} />
        )}

        <div className="flex items-center justify-end gap-3">
          <Button variant="primary" loading={busy} disabled={chosen.length === 0 || count === 0 || tikTokIncomplete} onClick={apply}>
            {mode === "add" ? "Aggiungi" : "Togli"} {chosen.length > 0 ? `${chosen.length === 1 ? "l'account" : `${chosen.length} account`}` : ""}
            {scope === "selected" ? ` a ${selected.length} post` : " a tutti i prossimi"}
          </Button>
        </div>

        {error ? <Alert tone="bad">{errorMessage(error)}</Alert> : null}
        {results && (
          <div className="space-y-2">
            <Alert tone={problems.length === 0 ? "ok" : "warn"}>
              {changed === 0 ? "Nessun post cambiato." : changed === 1 ? "Un post cambiato." : `${changed} post cambiati.`}
              {problems.length > 0 && ` ${problems.length === 1 ? "Uno è rimasto" : `${problems.length} sono rimasti`} com'era, ecco perché:`}
            </Alert>
            {problems.length > 0 && (
              <ul className="divide-y divide-line rounded-md border border-line">
                {problems.map((r) => (
                  <li key={r.postId} className="px-3 py-2">
                    <p className="truncate text-[0.8125rem] text-fg">{r.text || "Senza testo"} <span className="text-xs text-faint">· {formatDateTime(r.scheduledAtUtc)}</span></p>
                    <p className="text-xs text-bad">{r.problem}</p>
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}
      </div>
    </Panel>
  );
}
