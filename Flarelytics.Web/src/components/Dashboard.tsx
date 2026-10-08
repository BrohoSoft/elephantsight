import clsx from "clsx";
import { ArrowDownRight, ArrowUpRight, BarChart3, Link2, RefreshCw } from "lucide-react";
import { useState, type ReactNode } from "react";
import { Link } from "react-router";
import { useMetrics } from "../api/hooks";
import type { Metrics, Store, StoreTotals } from "../api/types";
import { countryName, formatDayYear, formatEur, formatInt, percentChange } from "./format";
import { formatDateTime } from "./org";
import { StoreGlyph } from "./StoreIcons";
import { TrendChart, type ChartMetric } from "./TrendChart";
import { Alert, EmptyState, Panel, Segmented, Spinner } from "./ui";

const PERIODS = [
  { value: "7", label: "7 giorni" },
  { value: "30", label: "30 giorni" },
  { value: "90", label: "90 giorni" },
  { value: "365", label: "12 mesi" },
] as const;

type Period = (typeof PERIODS)[number]["value"];

/**
 * I numeri di un'organizzazione o di un progetto: filtri in alto, indicatori,
 * andamento, paesi. `children` riceve le metriche per le parti che cambiano
 * fra le due pagine (l'elenco dei progetti nella panoramica).
 */
export function Dashboard({ orgId, projectId, children }: { orgId: string; projectId?: string; children?: (m: Metrics) => ReactNode }) {
  const [period, setPeriod] = useState<Period>("30");
  const [chartMetric, setChartMetric] = useState<ChartMetric>("downloads");
  const metrics = useMetrics(orgId, Number(period), projectId);

  if (metrics.isPending) {
    return <div className="flex h-40 items-center justify-center"><Spinner /></div>;
  }
  if (metrics.error || !metrics.data) return <Alert tone="bad">Non riesco a caricare i dati.</Alert>;

  const m = metrics.data;

  if (!m.hasLinkedApps) {
    return (
      <Panel>
        <EmptyState icon={<Link2 className="size-5" />} title="Nessuna app collegata">
          Collega un'app App Store o Google Play a {projectId ? "questo progetto" : "un progetto"} per vedere qui download e ricavi.
        </EmptyState>
      </Panel>
    );
  }

  if (!m.to) {
    return (
      <Panel>
        <EmptyState icon={<RefreshCw className="size-5" />} title="I dati stanno arrivando">
          La prima sincronizzazione scarica un anno di storico e può richiedere qualche minuto. Lo stato è in{" "}
          <Link to={`/o/${orgId}/credentials`} className="text-fg underline underline-offset-4">Chiavi degli store</Link>.
        </EmptyState>
      </Panel>
    );
  }

  const covers = (store: Store, metric: string) => m.coverage.find((c) => c.store === store)?.metrics.includes(metric) ?? false;
  const total = (pick: (t: StoreTotals) => number) => m.byStore.reduce((sum, t) => sum + pick(t), 0);
  // Uno store che non fornisce la metrica non compare nella divisione: niente
  // "0 €" per Google quando i suoi ricavi semplicemente non sono collegati.
  const split = (metric: string, pick: (t: StoreTotals) => number) =>
    Object.fromEntries(m.byStore.filter((t) => covers(t.store, metric)).map((t) => [t.store, pick(t)])) as Partial<Record<Store, number>>;
  const missing = (metric: string) => m.byStore.some((t) => !covers(t.store, metric));

  // I ricavi netti di Google arrivano con il report mensile: se il periodo
  // va oltre l'ultimo mese disponibile, la card lo dice.
  const googleThrough = m.coverage.find((c) => c.store === "GooglePlay")?.proceedsThrough;
  const proceedsNote = missing("proceeds")
    ? "Google Play non ancora incluso"
    : googleThrough && m.to && googleThrough < m.to && m.byStore.some((t) => t.store === "GooglePlay")
      ? `Google Play fino al ${formatDayYear(googleThrough)}`
      : undefined;

  return (
    <div className={clsx("space-y-6", metrics.isFetching && "opacity-70 transition-opacity")}>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <Segmented value={period} onChange={setPeriod} options={PERIODS.map((p) => ({ value: p.value, label: p.label }))} />
        <p className="text-xs text-faint">
          Dati dal {formatDayYear(m.from!)} al {formatDayYear(m.to)}
          {m.lastSyncAtUtc && <> · aggiornati {formatDateTime(m.lastSyncAtUtc)}</>}
        </p>
      </div>

      {m.hasUnconvertedAmounts && (
        <Alert tone="warn">Una parte dei ricavi è in valute per cui la BCE non pubblica un cambio, e non è inclusa nei totali in euro.</Alert>
      )}

      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Kpi label="Download" hint="Prime installazioni: su Google Play, utenti che installano per la prima volta" value={formatInt(total((t) => t.downloads))}
          split={split("downloads", (t) => t.downloads)} format={formatInt} change={percentChange(total((t) => t.downloads), m.previousDownloads)} days={m.days} />
        <Kpi label="Ricavi netti" hint="Al netto di commissioni e tasse" value={formatEur(total((t) => t.proceedsEur))}
          split={split("proceeds", (t) => t.proceedsEur)} format={formatEur} change={percentChange(total((t) => t.proceedsEur), m.previousProceedsEur)} days={m.days}
          note={proceedsNote} />
        <Kpi label="Acquisti in-app" value={formatInt(total((t) => t.inAppPurchases))} split={split("inAppPurchases", (t) => t.inAppPurchases)} format={formatInt}
          note={missing("inAppPurchases") ? "Google Play non ancora incluso" : undefined} />
        <Kpi label="Disinstallazioni" hint="Apple non comunica le disinstallazioni" value={formatInt(total((t) => t.uninstalls))}
          split={split("uninstalls", (t) => t.uninstalls)} format={formatInt} note={missing("uninstalls") ? "Solo Google Play" : undefined} />
      </div>

      <Panel
        title="Andamento"
        actions={
          <Segmented
            value={chartMetric}
            onChange={setChartMetric}
            options={[{ value: "downloads", label: "Download" }, { value: "proceeds", label: "Ricavi" }]}
          />
        }
      >
        <div className="p-4">
          <TrendChart metrics={m} metric={chartMetric} unavailable={(["AppStore", "GooglePlay"] as Store[]).filter((st) => !covers(st, chartMetric))} />
        </div>
      </Panel>

      <div className={clsx("grid gap-6", children ? "lg:grid-cols-2" : "")}>
        {children?.(m)}
        <CountryPanel metrics={m} />
      </div>
    </div>
  );
}

function Kpi({ label, hint, value, split, format, change, days, note }: {
  label: string;
  hint?: string;
  value: string;
  split: Partial<Record<Store, number>>;
  format: (n: number) => string;
  change?: number | null;
  days?: number;
  /** Quando il totale non comprende tutti gli store: lo si dice, non lo si lascia intuire. */
  note?: string;
}) {
  return (
    <div className="rounded-lg border border-line bg-panel p-4" title={hint}>
      <p className="text-xs text-muted">{label}</p>
      <p className="mt-2 text-2xl font-medium tracking-tight text-fg tabular-nums">{value}</p>
      {change !== undefined && (
        <p className="mt-1 h-4 text-xs text-muted tabular-nums">
          {change === null ? (
            <span className="text-faint">nessun confronto</span>
          ) : (
            <span className="inline-flex items-center gap-0.5">
              {change >= 0 ? <ArrowUpRight className="size-3.5" /> : <ArrowDownRight className="size-3.5" />}
              {`${change >= 0 ? "+" : ""}${(change * 100).toFixed(0)}%`}
              <span className="ml-1 hidden text-faint sm:inline">vs {days} giorni prima</span>
            </span>
          )}
        </p>
      )}
      {note && <p className="mt-1 text-[11px] text-faint">{note}</p>}
      <div className="mt-3 flex gap-4 text-xs text-muted">
        {(["AppStore", "GooglePlay"] as Store[]).map((s) => (
          <span key={s} className="inline-flex items-center gap-1.5" title={s === "AppStore" ? "App Store" : "Google Play"}>
            <StoreGlyph store={s} className={clsx("size-3", s === "AppStore" ? "text-ios" : "text-android")} />
            <span className="tabular-nums">{split[s] !== undefined ? format(split[s]!) : "—"}</span>
          </span>
        ))}
      </div>
    </div>
  );
}

function CountryPanel({ metrics }: { metrics: Metrics }) {
  const max = Math.max(1, ...metrics.countries.map((c) => c.downloads));

  return (
    <Panel title="Paesi" description="I primi per download nel periodo.">
      {metrics.countries.length === 0 ? (
        <EmptyState icon={<BarChart3 className="size-5" />} title="Nessun dato nel periodo" />
      ) : (
        <table className="w-full text-[13px]">
          <thead className="text-xs text-muted">
            <tr className="border-b border-line">
              <th className="px-4 py-2 text-left font-medium">Paese</th>
              <th className="px-4 py-2 text-right font-medium">Download</th>
              <th className="px-4 py-2 text-right font-medium">Ricavi netti</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-line">
            {metrics.countries.map((c) => (
              <tr key={c.countryCode}>
                <td className="px-4 py-2">
                  <span className="block truncate text-fg">{countryName(c.countryCode)}</span>
                  {/* Una barra sola, di un solo tono: è una grandezza, non un'identità. */}
                  <span className="mt-1 block h-1 rounded-full bg-panel-2">
                    <span className="block h-1 rounded-full bg-muted/50" style={{ width: `${(c.downloads / max) * 100}%` }} />
                  </span>
                </td>
                <td className="px-4 py-2 text-right tabular-nums">{formatInt(c.downloads)}</td>
                <td className="px-4 py-2 text-right tabular-nums text-muted">{formatEur(c.proceedsEur)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Panel>
  );
}
