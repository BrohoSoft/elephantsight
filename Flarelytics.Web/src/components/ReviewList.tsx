import clsx from "clsx";
import { MessageSquareReply, Star } from "lucide-react";
import { useState } from "react";
import { Link } from "react-router";
import { errorMessage } from "../api/client";
import { canAdmin, keys, useApiMutation, useReviews, type ReviewFilters } from "../api/hooks";
import type { ReviewItem, Store } from "../api/types";
import { AppIcon } from "./AppIcon";
import { formatDateTime, useOrg } from "./org";
import { StoreGlyph, storeName } from "./StoreIcons";
import { Alert, Badge, Button, EmptyState, PageLoader, Panel, Segmented, Select, Textarea } from "./ui";

const REPLY_LIMIT: Record<Store, number> = { AppStore: 5970, GooglePlay: 350 };

/**
 * Le recensioni di App Store e Google Play in una lista sola, dalla più
 * recente, con i filtri e la risposta in linea. Con <c>projectId</c> mostra
 * solo quelle di un progetto.
 */
export function ReviewList({ projectId }: { projectId?: string }) {
  const org = useOrg();
  const [store, setStore] = useState<"all" | Store>("all");
  const [rating, setRating] = useState<string>("");
  const [unanswered, setUnanswered] = useState(false);
  const [page, setPage] = useState(1);

  const filters: ReviewFilters = { projectId, store: store === "all" ? undefined : store, rating: rating ? Number(rating) : undefined, unanswered, page };
  const reviews = useReviews(org.id, filters);

  if (reviews.isPending) return <PageLoader />;
  if (reviews.error) return <Alert tone="bad">{errorMessage(reviews.error)}</Alert>;
  const data = reviews.data;
  const pages = Math.max(1, Math.ceil(data.total / data.pageSize));

  return (
    <div className="space-y-6">
      {data.sync.filter((s) => s.lastError).map((s) => (
        <Alert key={`${s.store}-${s.appId}`} tone="warn" title={`Recensioni ${storeName(s.store)} non aggiornate`}>{s.lastError}</Alert>
      ))}

      {data.summary.length > 0 && (
        <div className="grid gap-3 md:grid-cols-2">
          {data.summary.map((s) => (
            <div key={s.store} className="flex items-center gap-5 rounded-lg border border-line bg-panel p-4">
              <div>
                <p className="flex items-center gap-1.5 text-xs text-muted"><StoreGlyph store={s.store} className={clsx("size-3", s.store === "AppStore" ? "text-ios" : "text-android")} />{storeName(s.store)}</p>
                <p className="mt-1 text-2xl font-medium tracking-tight text-fg tabular-nums">{s.average.toFixed(1)}</p>
                <p className="text-xs text-faint">{s.count} recensioni</p>
              </div>
              <div className="flex-1 space-y-1">
                {[5, 4, 3, 2, 1].map((star) => {
                  const n = s.distribution[star - 1];
                  return (
                    <div key={star} className="flex items-center gap-2 text-[11px] text-muted">
                      <span className="w-3 text-right tabular-nums">{star}</span>
                      <span className="h-1.5 flex-1 overflow-hidden rounded-full bg-panel-2">
                        <span className="block h-full rounded-full bg-muted/50" style={{ width: `${s.count ? (n / s.count) * 100 : 0}%` }} />
                      </span>
                      <span className="w-8 tabular-nums">{n}</span>
                    </div>
                  );
                })}
              </div>
            </div>
          ))}
        </div>
      )}

      <div className="flex flex-wrap items-center gap-3">
        <Segmented value={store} onChange={(v) => { setStore(v); setPage(1); }}
          options={[{ value: "all", label: "Tutti" }, { value: "AppStore", label: "App Store" }, { value: "GooglePlay", label: "Google Play" }]} />
        <Select className="w-36" value={rating} onChange={(e) => { setRating(e.target.value); setPage(1); }}>
          <option value="">Tutte le stelle</option>
          {[5, 4, 3, 2, 1].map((n) => <option key={n} value={n}>{n} {n === 1 ? "stella" : "stelle"}</option>)}
        </Select>
        <label className="flex items-center gap-2 text-[13px] text-muted">
          <input type="checkbox" className="accent-[var(--brand)]" checked={unanswered} onChange={(e) => { setUnanswered(e.target.checked); setPage(1); }} />
          Senza risposta
        </label>
        <span className="ml-auto text-xs text-faint">{data.total} recensioni</span>
      </div>

      {data.items.length === 0 ? (
        <Panel><EmptyState title="Nessuna recensione">Le recensioni arrivano con la sincronizzazione delle chiavi degli store.</EmptyState></Panel>
      ) : (
        <Panel>
          <ul className="divide-y divide-line">
            {data.items.map((r) => <ReviewRow key={r.id} review={r} showProject={!projectId} />)}
          </ul>
        </Panel>
      )}

      {pages > 1 && (
        <div className="flex items-center justify-center gap-2">
          <Button size="sm" disabled={page <= 1} onClick={() => setPage(page - 1)}>Precedenti</Button>
          <span className="text-xs text-muted">{page} di {pages}</span>
          <Button size="sm" disabled={page >= pages} onClick={() => setPage(page + 1)}>Successive</Button>
        </div>
      )}
    </div>
  );
}

function Stars({ value }: { value: number }) {
  return (
    <span className="inline-flex" aria-label={`${value} stelle su 5`}>
      {[1, 2, 3, 4, 5].map((n) => <Star key={n} className={clsx("size-3.5", n <= value ? "fill-warn text-warn" : "text-line-strong")} />)}
    </span>
  );
}

function ReviewRow({ review: r, showProject }: { review: ReviewItem; showProject: boolean }) {
  const org = useOrg();
  const [replying, setReplying] = useState(false);
  const [text, setText] = useState(r.replyText ?? "");
  const limit = REPLY_LIMIT[r.store];
  const reply = useApiMutation(() => ({ path: `/orgs/${org.id}/reviews/${r.id}/reply`, body: { text } }), [keys.reviews(org.id)]);

  return (
    <li className="px-4 py-4">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <Stars value={r.rating} />
        {r.title && <span className="text-[13px] font-medium text-fg">{r.title}</span>}
        <span className="ml-auto flex items-center gap-2 text-xs text-faint">
          <StoreGlyph store={r.store} className={clsx("size-3", r.store === "AppStore" ? "text-ios" : "text-android")} />
          {formatDateTime(r.writtenAtUtc)}
        </span>
      </div>
      <p className="mt-1.5 text-[13px] whitespace-pre-line text-fg">{r.body}</p>
      <p className="mt-1.5 flex flex-wrap items-center gap-2 text-xs text-muted">
        {showProject && r.projectId && (
          <Link to={`/o/${org.id}/projects/${r.projectId}/reviews`} className="inline-flex items-center gap-1.5 hover:text-fg">
            <AppIcon src={r.iconUrl} name={r.projectName ?? "?"} size="sm" className="!size-4 !rounded" />{r.projectName}
          </Link>
        )}
        {r.author && <span>{r.author}</span>}
        {r.locale && <Badge>{r.locale}</Badge>}
        {r.appVersion && <span>v{r.appVersion}</span>}
      </p>

      {r.replyText && !replying && (
        <div className="mt-3 rounded-md border-l-2 border-brand/60 bg-panel-2 px-3 py-2">
          <p className="text-xs text-muted">
            La tua risposta{r.repliedAtUtc && ` · ${formatDateTime(r.repliedAtUtc)}`}{r.replyState === "PENDING_PUBLISH" && " · in attesa di pubblicazione"}
          </p>
          <p className="mt-1 text-[13px] whitespace-pre-line text-fg">{r.replyText}</p>
        </div>
      )}

      {canAdmin(org.role) && (replying ? (
        <div className="mt-3 space-y-2">
          <Textarea rows={3} className="font-sans text-[13px]" maxLength={limit} value={text} onChange={(e) => setText(e.target.value)} autoFocus />
          <div className="flex items-center gap-2">
            <span className={clsx("text-xs", text.length > limit ? "text-bad" : "text-faint")}>{text.length}/{limit}</span>
            {reply.error && <span className="text-xs text-bad">{errorMessage(reply.error)}</span>}
            <Button size="sm" variant="ghost" className="ml-auto" onClick={() => setReplying(false)}>Annulla</Button>
            <Button size="sm" variant="primary" loading={reply.isPending} disabled={!text.trim()} onClick={() => reply.mutate(undefined, { onSuccess: () => setReplying(false) })}>
              {r.replyText ? "Aggiorna la risposta" : "Pubblica la risposta"}
            </Button>
          </div>
        </div>
      ) : (
        <Button size="sm" variant="ghost" className="mt-2 -ml-2" icon={<MessageSquareReply className="size-3" />} onClick={() => setReplying(true)}>
          {r.replyText ? "Modifica la risposta" : "Rispondi"}
        </Button>
      ))}
    </li>
  );
}
