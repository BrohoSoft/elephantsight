const integer = new Intl.NumberFormat("it-IT");
const eur = new Intl.NumberFormat("it-IT", { style: "currency", currency: "EUR", minimumFractionDigits: 2, maximumFractionDigits: 2 });
const eurRound = new Intl.NumberFormat("it-IT", { style: "currency", currency: "EUR", maximumFractionDigits: 0 });
const compact = new Intl.NumberFormat("it-IT", { notation: "compact", maximumFractionDigits: 1 });
const regions = new Intl.DisplayNames(["it"], { type: "region" });
const dayMonth = new Intl.DateTimeFormat("it-IT", { day: "numeric", month: "short" });
const dayMonthYear = new Intl.DateTimeFormat("it-IT", { day: "numeric", month: "short", year: "numeric" });

export const formatInt = (n: number) => integer.format(n);

/** Due decimali per gli importi piccoli, nessuno da 10.000 € in su: i centesimi lì sono rumore. */
export const formatEur = (n: number) => (Math.abs(n) >= 10_000 ? eurRound : eur).format(n);

export const formatCompact = (n: number) => compact.format(n);

export function countryName(code: string) {
  try {
    return regions.of(code) ?? code;
  } catch {
    return code;
  }
}

/** Le date dei report sono giorni (aaaa-mm-gg), senza ora: si leggono a mezzogiorno per non scivolare di fuso. */
export const parseDay = (iso: string) => new Date(`${iso}T12:00:00`);
export const formatDay = (iso: string) => dayMonth.format(parseDay(iso));
export const formatDayYear = (iso: string) => dayMonthYear.format(parseDay(iso));

export function percentChange(current: number, previous: number): number | null {
  if (previous === 0) return null;
  return (current - previous) / Math.abs(previous);
}
