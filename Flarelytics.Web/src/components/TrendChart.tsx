import { useEffect, useMemo, useRef, useState } from "react";
import type { Metrics, Store } from "../api/types";
import { formatCompact, formatDay, formatEur, formatInt, parseDay } from "./format";
import { storeName } from "./StoreIcons";

export type ChartMetric = "downloads" | "proceeds";

interface Bucket {
  key: string;
  label: string;
  values: Record<Store, number>;
}

const STORES: Store[] = ["AppStore", "GooglePlay"];
const COLOR: Record<Store, string> = { AppStore: "var(--ios)", GooglePlay: "var(--android)" };

const HEIGHT = 220;
const MARGIN = { top: 8, right: 8, bottom: 24, left: 52 };
const MAX_BAR = 24;
const GAP = 2;
const RADIUS = 4;

/**
 * Barre impilate per giorno: App Store in basso, Google Play sopra. Si legge
 * il totale (l'altezza della colonna) e la parte di ciascuno store (i due
 * segmenti). Un valore alla volta, download o ricavi: due misure con scale
 * diverse non stanno sullo stesso asse.
 *
 * Oltre i 90 giorni le barre diventano settimanali, altrimenti sarebbero più
 * sottili di un pixel.
 */
export function TrendChart({ metrics, metric, unavailable = [] }: { metrics: Metrics; metric: ChartMetric; unavailable?: Store[] }) {
  const container = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(0);
  const [hover, setHover] = useState<number | null>(null);
  const [asTable, setAsTable] = useState(false);

  useEffect(() => {
    const el = container.current;
    if (!el) return;
    const observer = new ResizeObserver(([entry]) => setWidth(entry.contentRect.width));
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  const buckets = useMemo(() => buildBuckets(metrics, metric), [metrics, metric]);
  const format = metric === "downloads" ? formatInt : formatEur;
  const totals = STORES.map((s) => buckets.reduce((sum, b) => sum + b.values[s], 0));

  const plotWidth = Math.max(0, width - MARGIN.left - MARGIN.right);
  const plotHeight = HEIGHT - MARGIN.top - MARGIN.bottom;
  const max = Math.max(0, ...buckets.map((b) => b.values.AppStore + b.values.GooglePlay));
  const { top, ticks } = niceScale(max);
  const band = buckets.length > 0 ? plotWidth / buckets.length : 0;
  const barWidth = Math.max(1, Math.min(MAX_BAR, band * 0.7));
  const y = (v: number) => MARGIN.top + plotHeight - (top === 0 ? 0 : (v / top) * plotHeight);
  const labelEvery = Math.max(1, Math.ceil(buckets.length / Math.max(1, Math.floor(plotWidth / 70))));
  const hovered = hover !== null ? buckets[hover] : null;

  return (
    <div>
      <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
        {/* La legenda porta anche i totali: l'identità degli store non dipende solo dal colore. */}
        <div className="flex flex-wrap gap-x-5 gap-y-1 text-xs">
          {STORES.map((s, i) => (
            <span key={s} className="inline-flex items-center gap-1.5 text-muted">
              <span className="size-2.5 rounded-sm" style={{ background: COLOR[s] }} />
              {storeName(s)}{" "}
              {unavailable.includes(s)
                ? <span className="text-faint" title="Questo dato non è ancora collegato per questo store">non disponibile</span>
                : <span className="font-medium text-fg tabular-nums">{format(totals[i])}</span>}
            </span>
          ))}
        </div>
        <button type="button" className="text-xs text-muted hover:text-fg" onClick={() => setAsTable(!asTable)}>
          {asTable ? "Mostra grafico" : "Mostra tabella"}
        </button>
      </div>

      {asTable ? (
        <div className="max-h-[260px] overflow-y-auto rounded-md border border-line">
          <table className="w-full text-[13px]">
            <thead className="sticky top-0 bg-panel-2 text-xs text-muted">
              <tr>
                <th className="px-3 py-2 text-left font-medium">{metrics.days > 90 ? "Settimana dal" : "Giorno"}</th>
                {STORES.map((s) => <th key={s} className="px-3 py-2 text-right font-medium">{storeName(s)}</th>)}
                <th className="px-3 py-2 text-right font-medium">Totale</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-line">
              {[...buckets].reverse().map((b) => (
                <tr key={b.key}>
                  <td className="px-3 py-1.5 text-muted">{b.label}</td>
                  {STORES.map((s) => <td key={s} className="px-3 py-1.5 text-right tabular-nums">{format(b.values[s])}</td>)}
                  <td className="px-3 py-1.5 text-right font-medium tabular-nums">{format(b.values.AppStore + b.values.GooglePlay)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <div ref={container} className="relative" onPointerLeave={() => setHover(null)}>
          {width > 0 && (
            <svg width={width} height={HEIGHT} role="img" aria-label={`${metric === "downloads" ? "Download" : "Ricavi"} per ${metrics.days > 90 ? "settimana" : "giorno"}, App Store e Google Play`}>
              {ticks.map((t) => (
                <g key={t}>
                  <line x1={MARGIN.left} x2={width - MARGIN.right} y1={y(t)} y2={y(t)} stroke="var(--grid)" strokeWidth={1} />
                  <text x={MARGIN.left - 8} y={y(t)} dy="0.32em" textAnchor="end" fontSize={11} fill="var(--faint)">
                    {metric === "downloads" ? formatCompact(t) : `${formatCompact(t)} €`}
                  </text>
                </g>
              ))}

              {buckets.map((b, i) => {
                const cx = MARGIN.left + band * i + band / 2;
                const x = cx - barWidth / 2;
                const ios = b.values.AppStore;
                const android = b.values.GooglePlay;
                const yIos = y(ios);
                const yTop = y(ios + android);
                // Spazio di 2px fra i segmenti, solo se ci sono entrambi.
                const androidBottom = ios > 0 ? yIos - GAP : y(0);

                return (
                  <g key={b.key}>
                    {ios > 0 && <path d={bar(x, yIos, barWidth, y(0) - yIos, android > 0 ? 0 : RADIUS)} fill={COLOR.AppStore} opacity={hover === null || hover === i ? 1 : 0.45} />}
                    {android > 0 && androidBottom > yTop && (
                      <path d={bar(x, yTop, barWidth, androidBottom - yTop, RADIUS)} fill={COLOR.GooglePlay} opacity={hover === null || hover === i ? 1 : 0.45} />
                    )}
                    {i % labelEvery === 0 && (
                      <text x={cx} y={HEIGHT - 6} textAnchor="middle" fontSize={11} fill="var(--faint)">{b.label}</text>
                    )}
                    {/* L'area sensibile è tutta la colonna, non solo la barra. */}
                    <rect
                      x={MARGIN.left + band * i}
                      y={MARGIN.top}
                      width={band}
                      height={plotHeight}
                      fill="transparent"
                      tabIndex={0}
                      aria-label={`${b.label}: ${STORES.map((s) => `${storeName(s)} ${format(b.values[s])}`).join(", ")}`}
                      onPointerEnter={() => setHover(i)}
                      onFocus={() => setHover(i)}
                      onBlur={() => setHover(null)}
                      className="outline-none"
                    />
                  </g>
                );
              })}

              <line x1={MARGIN.left} x2={width - MARGIN.right} y1={y(0)} y2={y(0)} stroke="var(--line-strong)" strokeWidth={1} />
            </svg>
          )}

          {hovered && hover !== null && (
            <div
              className="pointer-events-none absolute top-0 z-10 min-w-40 rounded-md border border-line-strong bg-panel px-3 py-2 text-xs shadow-lg"
              style={tooltipPosition(MARGIN.left + band * hover, band, width)}
            >
              <p className="mb-1.5 text-muted">{metrics.days > 90 ? `Settimana dal ${hovered.label}` : hovered.label}</p>
              {STORES.map((s) => (
                <p key={s} className="flex items-center justify-between gap-4">
                  <span className="inline-flex items-center gap-1.5 text-muted">
                    <span className="size-2 rounded-sm" style={{ background: COLOR[s] }} />
                    {storeName(s)}
                  </span>
                  <span className="font-medium text-fg tabular-nums">{format(hovered.values[s])}</span>
                </p>
              ))}
              <p className="mt-1 flex justify-between gap-4 border-t border-line pt-1">
                <span className="text-muted">Totale</span>
                <span className="font-medium text-fg tabular-nums">{format(hovered.values.AppStore + hovered.values.GooglePlay)}</span>
              </p>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/**
 * Il tooltip sta di fianco alla colonna, non sopra: coprirebbe proprio la
 * barra che si sta guardando. A destra nella prima metà del grafico, a
 * sinistra nella seconda, così non esce mai dal bordo.
 */
function tooltipPosition(columnLeft: number, band: number, width: number) {
  const gap = 8;
  return columnLeft + band / 2 < width / 2
    ? { left: columnLeft + band + gap }
    : { right: width - columnLeft + gap };
}

/** Un rettangolo con gli angoli in alto arrotondati e la base dritta, appoggiata all'asse. */
function bar(x: number, y: number, w: number, h: number, r: number) {
  const radius = Math.min(r, w / 2, h);
  return `M${x},${y + h} V${y + radius} Q${x},${y} ${x + radius},${y} H${x + w - radius} Q${x + w},${y} ${x + w},${y + radius} V${y + h} Z`;
}

/** Tacche "tonde" (1, 2, 2,5, 5 × 10ⁿ): 0 / 500 / 1.000, non 0 / 437 / 874. */
function niceScale(max: number): { top: number; ticks: number[] } {
  if (max <= 0) return { top: 1, ticks: [0] };
  const rough = max / 4;
  const power = 10 ** Math.floor(Math.log10(rough));
  const step = [1, 2, 2.5, 5, 10].map((m) => m * power).find((s) => s >= rough)!;
  const top = Math.ceil(max / step) * step;
  return { top, ticks: Array.from({ length: Math.round(top / step) + 1 }, (_, i) => i * step) };
}

function buildBuckets(metrics: Metrics, metric: ChartMetric): Bucket[] {
  if (!metrics.from || !metrics.to) return [];

  const weekly = metrics.days > 90;
  const byKey = new Map<string, Bucket>();
  const keyOf = (iso: string) => {
    if (!weekly) return iso;
    const d = parseDay(iso);
    const monday = new Date(d);
    monday.setDate(d.getDate() - ((d.getDay() + 6) % 7));
    return monday.toISOString().slice(0, 10);
  };

  // Tutti i giorni del periodo, anche quelli senza dati: un giorno a zero è
  // un dato, e saltarlo stringerebbe l'asse del tempo.
  for (let d = parseDay(metrics.from); d <= parseDay(metrics.to); d.setDate(d.getDate() + 1)) {
    const iso = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
    const key = keyOf(iso);
    if (!byKey.has(key)) byKey.set(key, { key, label: formatDay(key), values: { AppStore: 0, GooglePlay: 0 } });
  }

  for (const p of metrics.daily) {
    const bucket = byKey.get(keyOf(p.date));
    if (bucket) bucket.values[p.store] += metric === "downloads" ? p.downloads : p.proceedsEur;
  }

  return [...byKey.values()];
}
