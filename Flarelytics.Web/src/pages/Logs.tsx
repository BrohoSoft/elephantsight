import { useInfiniteQuery } from "@tanstack/react-query";
import clsx from "clsx";
import { RefreshCw, ScrollText, Search } from "lucide-react";
import { useState } from "react";
import { useSearchParams } from "react-router";
import { errorMessage, request } from "../api/client";
import type { LogItem, LogPage } from "../api/types";
import { useOrg } from "../components/org";
import { Alert, Badge, Button, EmptyState, Input, PageHeader, PageLoader, Panel, Segmented, Select } from "../components/ui";

type Level = "Information" | "Warning" | "Error";

const levelBadge: Record<string, { label: string; tone: "neutral" | "warn" | "bad" }> = {
  Information: { label: "Info", tone: "neutral" },
  Warning: { label: "Avviso", tone: "warn" },
  Error: { label: "Errore", tone: "bad" },
  Critical: { label: "Critico", tone: "bad" },
};

const timeFormat = new Intl.DateTimeFormat("it-IT", { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit", second: "2-digit" });

/**
 * Cosa è successo: pubblicazioni riuscite e fallite (con il motivo della
 * rete), tentativi, sincronizzazioni con gli store, token rinnovati, errori.
 * Gli ultimi 30 giorni, dal più recente. L'owner vede anche i messaggi di
 * sistema, che non sono di nessuna organizzazione.
 */
export function LogsPage() {
  const org = useOrg();
  // Il filtro può arrivare dall'indirizzo (dalla panoramica: ?level=Warning).
  const [params] = useSearchParams();
  const [level, setLevel] = useState<Level>(() => (["Information", "Warning", "Error"].includes(params.get("level") ?? "") ? params.get("level") as Level : "Information"));
  const [area, setArea] = useState("");
  const [text, setText] = useState("");
  const [q, setQ] = useState("");

  const logs = useInfiniteQuery({
    queryKey: ["org", org.id, "logs", level, area, q],
    initialPageParam: null as number | null,
    queryFn: ({ pageParam }) => {
      const params = new URLSearchParams({ level });
      if (area) params.set("area", area);
      if (q) params.set("q", q);
      if (pageParam) params.set("before", String(pageParam));
      return request<LogPage>(`/orgs/${org.id}/logs?${params}`);
    },
    getNextPageParam: (last) => (last.hasMore ? last.items[last.items.length - 1].id : null),
    refetchInterval: 30_000,
  });

  const items = logs.data?.pages.flatMap((p) => p.items) ?? [];

  return (
    <>
      <PageHeader
        title="Log"
        description="Cosa ha fatto ElephantSight negli ultimi 30 giorni: post pubblicati o rifiutati (con il motivo dato dalla rete), tentativi, sincronizzazioni con gli store, errori. Si aggiorna da sola ogni 30 secondi."
        actions={<Button icon={<RefreshCw className={clsx("size-3.5", logs.isFetching && "animate-spin")} />} onClick={() => logs.refetch()}>Aggiorna</Button>}
      />

      <div className="mb-4 flex flex-wrap items-center gap-2">
        <Segmented value={level} onChange={setLevel}
          options={[{ value: "Information", label: "Tutto" }, { value: "Warning", label: "Avvisi ed errori" }, { value: "Error", label: "Solo errori" }]} />
        <Select value={area} onChange={(e) => setArea(e.target.value)} className="w-36" aria-label="Area">
          <option value="">Tutte le aree</option>
          <option value="Social">Social</option>
          <option value="Store">Store</option>
          <option value="Sistema">Sistema</option>
        </Select>
        <form className="flex min-w-56 flex-1 gap-2" onSubmit={(e) => { e.preventDefault(); setQ(text.trim()); }}>
          <Input value={text} onChange={(e) => setText(e.target.value)} placeholder="Cerca: Threads, rifiutato, un id…" aria-label="Cerca nei log" />
          <Button type="submit" icon={<Search className="size-3.5" />} aria-label="Cerca" />
        </form>
      </div>

      {logs.isPending ? <PageLoader /> : logs.error ? <Alert tone="bad">{errorMessage(logs.error)}</Alert> : items.length === 0 ? (
        <div className="rounded-lg border border-dashed border-line-strong">
          <EmptyState icon={<ScrollText className="size-5" />} title="Nessun messaggio">
            {q || area || level !== "Information" ? "Niente con questi filtri." : "Qui compariranno pubblicazioni, sincronizzazioni ed errori."}
          </EmptyState>
        </div>
      ) : (
        <Panel>
          <ul className="divide-y divide-line">
            {items.map((item) => <LogRow key={item.id} item={item} />)}
          </ul>
          {logs.hasNextPage && (
            <div className="border-t border-line p-3 text-center">
              <Button size="sm" loading={logs.isFetchingNextPage} onClick={() => logs.fetchNextPage()}>Più vecchi</Button>
            </div>
          )}
        </Panel>
      )}
    </>
  );
}

function LogRow({ item }: { item: LogItem }) {
  const badge = levelBadge[item.level] ?? levelBadge.Information;
  return (
    <li className="px-4 py-2.5">
      <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
        <span className="font-mono text-[0.6875rem] text-faint">{timeFormat.format(new Date(item.timestampUtc))}</span>
        <Badge tone={badge.tone}>{badge.label}</Badge>
        <span className="text-[0.6875rem] text-muted">{item.system ? "Sistema (istanza)" : item.area}</span>
      </div>
      <p className="mt-1 text-[0.8125rem] break-words whitespace-pre-wrap text-fg">{item.message}</p>
      {item.exception && (
        <details className="mt-1">
          <summary className="cursor-pointer text-xs text-muted hover:text-fg">Dettagli tecnici</summary>
          <pre className="mt-1 max-h-64 overflow-auto rounded border border-line bg-panel-2 p-2 text-[0.6875rem] text-muted">{item.exception}</pre>
        </details>
      )}
      <p className="mt-0.5 truncate font-mono text-[0.625rem] text-faint" title={item.category}>{item.category}</p>
    </li>
  );
}
