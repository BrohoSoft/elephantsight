import { useQueryClient } from "@tanstack/react-query";
import clsx from "clsx";
import { Inbox as InboxIcon, Trash2 } from "lucide-react";
import { useState } from "react";
import { Link } from "react-router";
import { errorMessage, request } from "../../api/client";
import { canAdmin, keys, useProjects, useSocialAccounts, useSocialInbox } from "../../api/hooks";
import { defaultPostOptions, type AssignResult, type PostOptions, type SocialPost } from "../../api/types";
import { formatDateTime, useOrg } from "../../components/org";
import { NetworkGlyph } from "../../components/SocialIcons";
import { Alert, Button, EmptyState, Field, Input, Mono, PageHeader, PageLoader, Panel, Segmented } from "../../components/ui";
import { commercialIncomplete, NetworkOptions } from "../../social/NetworkOptions";
import { PostEditor } from "../../social/PostEditor";
import { accountLabel } from "../../social/rules";

const pad = (n: number) => String(n).padStart(2, "0");

/**
 * La coda "Da programmare": i post mandati da altri programmi con una chiave
 * API. Qui si sceglie su quali account vanno e quando, uno per uno (si apre
 * l'editor) o tanti insieme (si selezionano e si assegnano).
 */
export function SocialInboxPage() {
  const org = useOrg();
  const admin = canAdmin(org.role);
  const queryClient = useQueryClient();
  const inbox = useSocialInbox(org.id);
  const accounts = useSocialAccounts(org.id);
  const projects = useProjects(org.id);

  const [selected, setSelected] = useState<string[]>([]);
  const [targets, setTargets] = useState<string[]>([]);
  const [when, setWhen] = useState<"suggested" | "fixed">("suggested");
  const tomorrow = new Date(Date.now() + 86_400_000);
  const [date, setDate] = useState(`${tomorrow.getFullYear()}-${pad(tomorrow.getMonth() + 1)}-${pad(tomorrow.getDate())}`);
  const [time, setTime] = useState("10:00");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [problems, setProblems] = useState<Record<string, string>>({});
  const [done, setDone] = useState<number | null>(null);
  const [editing, setEditing] = useState<SocialPost | null>(null);
  const [options, setOptions] = useState<PostOptions>(defaultPostOptions);
  const [commercial, setCommercial] = useState(false);

  if (inbox.isPending || accounts.isPending || projects.isPending) return <PageLoader />;
  const posts = inbox.data ?? [];

  const toggle = (id: string) => setSelected((s) => (s.includes(id) ? s.filter((x) => x !== id) : [...s, id]));
  const refresh = () => Promise.all([
    queryClient.invalidateQueries({ queryKey: keys.socialInbox(org.id) }),
    queryClient.invalidateQueries({ queryKey: keys.socialPosts(org.id) }),
  ]);

  async function assign() {
    setBusy(true);
    setError(null);
    setDone(null);
    try {
      const results = await request<AssignResult[]>(`/orgs/${org.id}/social/inbox/assign`, {
        method: "POST",
        body: { postIds: selected, accountIds: targets, scheduledAtUtc: when === "fixed" ? new Date(`${date}T${time}`).toISOString() : null, options },
      });
      setProblems(Object.fromEntries(results.filter((r) => r.problem).map((r) => [r.postId, r.problem!])));
      setSelected(results.filter((r) => !r.scheduled).map((r) => r.postId));
      setDone(results.filter((r) => r.scheduled).length);
      await refresh();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  }

  async function remove(id: string) {
    await request(`/orgs/${org.id}/social/posts/${id}`, { method: "DELETE" });
    setSelected((s) => s.filter((x) => x !== id));
    await refresh();
  }

  return (
    <>
      <PageHeader
        title="Da programmare"
        description={<>I post mandati da altri programmi (un CMS, uno script, un'automazione) con una <Link to={`/o/${org.id}/api-keys`} className="text-brand-fg hover:underline">chiave API</Link>. Scegli account e ora: da lì diventano post programmati come gli altri.</>}
      />

      {posts.length === 0 ? (
        <div className="rounded-lg border border-dashed border-line-strong">
          <EmptyState icon={<InboxIcon className="size-5" />} title="Nessun post in coda"
            action={admin && <Link to={`/o/${org.id}/api-keys`}><Button>Crea una chiave API</Button></Link>}>
            Un programma esterno manda i post qui con una chiave API: testo, immagini e, se vuole, una data proposta.
          </EmptyState>
        </div>
      ) : (
        <>
          {admin && (
            <Panel className="mb-4" title={selected.length > 0 ? `${selected.length} selezionati` : "Seleziona i post da programmare"}
              actions={<Button size="sm" variant="ghost" onClick={() => setSelected(selected.length === posts.length ? [] : posts.map((p) => p.id))}>
                {selected.length === posts.length ? "Deseleziona tutti" : "Seleziona tutti"}
              </Button>}>
              <div className="space-y-4 p-4">
                <Field label="Su quali account">
                  {accounts.data!.length === 0 ? (
                    <p className="text-[13px] text-muted">Nessun account collegato. <Link to={`/o/${org.id}/social/accounts`} className="text-brand-fg hover:underline">Collegane uno</Link>.</p>
                  ) : (
                    <div className="flex flex-wrap gap-1.5">
                      {accounts.data!.map((a) => {
                        const on = targets.includes(a.id);
                        return (
                          <button key={a.id} type="button" aria-pressed={on} disabled={a.status === "NeedsReconnect"}
                            onClick={() => setTargets((t) => (t.includes(a.id) ? t.filter((x) => x !== a.id) : [...t, a.id]))}
                            className={clsx("inline-flex h-8 items-center gap-1.5 rounded-md border px-2.5 text-[13px] transition-colors disabled:opacity-50",
                              on ? "border-brand/60 bg-brand/10 text-fg" : "border-line-strong bg-panel-2 text-muted hover:text-fg")}>
                            <NetworkGlyph network={a.network} className="size-3.5" />{accountLabel(a)}
                          </button>
                        );
                      })}
                    </div>
                  )}
                </Field>
                <NetworkOptions accounts={accounts.data!.filter((a) => targets.includes(a.id))}
                  media={posts.filter((p) => selected.includes(p.id)).flatMap((p) => p.media)}
                  options={options} onChange={setOptions} commercial={commercial} onCommercial={setCommercial} readOnly={false} />
                <div className="flex flex-wrap items-end gap-3">
                  <Segmented value={when} onChange={setWhen} options={[{ value: "suggested", label: "Alle date proposte" }, { value: "fixed", label: "Tutti alla stessa ora" }]} />
                  {when === "fixed" && (
                    <>
                      <Input type="date" className="w-40" value={date} onChange={(e) => setDate(e.target.value)} aria-label="Giorno" />
                      <Input type="time" className="w-28" value={time} onChange={(e) => setTime(e.target.value)} aria-label="Ora" />
                    </>
                  )}
                  <span className="flex-1" />
                  <Button variant="primary" loading={busy} disabled={selected.length === 0 || targets.length === 0
                    || (accounts.data!.some((a) => a.network === "TikTok" && targets.includes(a.id)) && (!options.tikTokPrivacy || commercialIncomplete(options, commercial)))}
                    onClick={assign}>
                    Programma {selected.length > 0 ? selected.length : ""}
                  </Button>
                </div>
                {done !== null && done > 0 && <Alert tone="ok">{done === 1 ? "Un post programmato" : `${done} post programmati`}: li trovi nel calendario.</Alert>}
                {Object.keys(problems).length > 0 && <Alert tone="warn">Alcuni post non vanno bene per gli account scelti: il motivo è sotto ciascuno. Correggili o scegli altri account.</Alert>}
                {error ? <Alert tone="bad">{errorMessage(error)}</Alert> : null}
              </div>
            </Panel>
          )}

          <Panel>
            <ul className="divide-y divide-line">
              {posts.map((p) => (
                <li key={p.id} className="flex gap-3 px-4 py-3">
                  {admin && (
                    <input type="checkbox" className="mt-1 accent-brand" checked={selected.includes(p.id)} onChange={() => toggle(p.id)} aria-label="Seleziona" />
                  )}
                  {p.media[0] ? <img src={p.media[0].url} alt={p.media[0].altText ?? ""} className="size-16 shrink-0 rounded object-cover" /> : <div className="size-16 shrink-0 rounded border border-dashed border-line-strong" />}
                  <button type="button" className="min-w-0 flex-1 text-left" onClick={() => setEditing(p)}>
                    <p className="line-clamp-3 text-[13px] whitespace-pre-line text-fg">{p.text || <span className="text-muted">Senza testo</span>}</p>
                    <p className="mt-1 text-xs text-muted">
                      {p.suggestedAtUtc ? <>proposto per il {formatDateTime(p.suggestedAtUtc)}</> : "senza data proposta"}
                      {p.source && <> · da {p.source}</>}
                      {p.media.length > 0 && <> · {p.media.length === 1 ? "1 immagine" : `${p.media.length} immagini`}</>}
                      {p.externalRef && <> · <Mono>{p.externalRef}</Mono></>}
                    </p>
                    {problems[p.id] && <p className="mt-1 text-xs text-bad">{problems[p.id]}</p>}
                  </button>
                  {admin && <Button size="sm" variant="ghost" icon={<Trash2 className="size-3" />} aria-label="Elimina" onClick={() => remove(p.id)} />}
                </li>
              ))}
            </ul>
          </Panel>
        </>
      )}

      {editing && <PostEditor post={editing} accounts={accounts.data!} projects={projects.data ?? []} admin={admin} onClose={() => setEditing(null)} />}
    </>
  );
}
