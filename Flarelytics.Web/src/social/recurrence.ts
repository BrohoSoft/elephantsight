import type { RecurrenceFrequency, RecurringPost, Weekday } from "../api/types";

/** La settimana da lunedì, come in Italia (e come la scorre il server). */
export const weekdays: { value: Weekday; short: string; long: string; article: string }[] = [
  { value: "Monday", short: "L", long: "lunedì", article: "il" },
  { value: "Tuesday", short: "M", long: "martedì", article: "il" },
  { value: "Wednesday", short: "M", long: "mercoledì", article: "il" },
  { value: "Thursday", short: "G", long: "giovedì", article: "il" },
  { value: "Friday", short: "V", long: "venerdì", article: "il" },
  { value: "Saturday", short: "S", long: "sabato", article: "il" },
  { value: "Sunday", short: "D", long: "domenica", article: "la" },
];

/** Il fuso del browser: le ore dei post ricorrenti valgono lì, anche con l'ora legale. */
export const browserTimeZone = () => Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";

/** Elenco all'italiana: "lunedì, mercoledì e venerdì". */
const list = (items: string[]) => (items.length <= 1 ? items.join("") : `${items.slice(0, -1).join(", ")} e ${items[items.length - 1]}`);

const unit: Record<RecurrenceFrequency, [string, string]> = {
  Daily: ["giorno", "giorni"],
  Weekly: ["settimana", "settimane"],
  Monthly: ["mese", "mesi"],
};

/** La regola in una frase: "Ogni 2 settimane il lunedì e il giovedì alle 18:30". */
export function describeRule(r: Pick<RecurringPost, "frequency" | "interval" | "daysOfWeek" | "timeOfDay" | "timeZone" | "startDate" | "endDate">): string {
  const [one, many] = unit[r.frequency];
  let text = r.interval === 1 ? `Ogni ${one}` : `Ogni ${r.interval} ${many}`;
  if (r.frequency === "Weekly") {
    const days = weekdays.filter((d) => r.daysOfWeek.includes(d.value)).map((d) => `${d.article} ${d.long}`);
    text += days.length === 7 ? ", tutti i giorni" : ` ${list(days)}`;
  }
  if (r.frequency === "Monthly") text += ` il giorno ${Number(r.startDate.slice(8, 10))}`;
  text += ` alle ${r.timeOfDay}`;
  if (r.timeZone !== browserTimeZone()) text += ` (ora di ${r.timeZone})`;
  if (r.endDate) text += `, fino al ${new Date(`${r.endDate}T12:00`).toLocaleDateString("it-IT", { day: "numeric", month: "long", year: "numeric" })}`;
  return text;
}
