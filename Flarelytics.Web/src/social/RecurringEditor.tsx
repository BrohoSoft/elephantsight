import { useQueryClient } from "@tanstack/react-query";
import clsx from "clsx";
import { Copy, Trash2 } from "lucide-react";
import { useState } from "react";
import { errorMessage, request } from "../api/client";
import { keys } from "../api/hooks";
import { defaultPostOptions, type PostOptions, type Project, type RecurrenceFrequency, type RecurringPost, type SocialAccount, type SocialMediaItem, type Weekday } from "../api/types";
import { canSeeProject, formatDateTime, useOrg } from "../components/org";
import { NetworkGlyph } from "../components/SocialIcons";
import { Alert, Button, Field, Input, Modal, Segmented, Textarea } from "../components/ui";
import { AccountPicker, accountsFor, MediaField, mediaHint, ProjectField, projectProblems } from "./EditorParts";
import { commercialIncomplete, NetworkOptions } from "./NetworkOptions";
import { browserTimeZone, describeRule, duplicateTemplate, weekdays } from "./recurrence";
import { accountLabel, countCharacters, optionProblems, problems } from "./rules";

const pad = (n: number) => String(n).padStart(2, "0");
const today = () => {
  const d = new Date();
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
};
/** Il giorno della settimana di una data "yyyy-MM-dd", nel formato del server. */
const weekdayOf = (date: string): Weekday =>
  (["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"] as const)[new Date(`${date}T12:00`).getDay()];

/**
 * Scrivere un post ricorrente: lo stesso contenuto di un post (account,
 * testo, immagini, scelte per rete) più la regola. Le uscite le crea il
 * server all'ora giusta, quindi una modifica vale per tutte quelle future.
 * Con <code>template</code> è un post nuovo che parte da una copia (vedi duplicateTemplate).
 */
export function RecurringEditor({ recurring, template, defaultProjectId, accounts, projects, admin, onClose, onDuplicate }: {
  recurring?: RecurringPost;
  /** Il progetto di un post ricorrente nuovo (dalla pagina di un progetto). */
  defaultProjectId?: string;
  /** Un post nuovo che parte da questi valori: "Duplica". */
  template?: RecurringPost;
  /** Chiude questo editor e ne apre uno nuovo con la copia. */
  onDuplicate?: (recurring: RecurringPost) => void;
  accounts: SocialAccount[];
  projects: Project[];
  admin: boolean;
  onClose: () => void;
}) {
  const org = useOrg();
  const queryClient = useQueryClient();
  const readOnly = !admin;
  // Da dove partono i campi: il post che si modifica, o quello che si duplica.
  const source = recurring ?? template;

  const [text, setText] = useState(source?.text ?? "");
  const [selected, setSelected] = useState<string[]>(source?.accountIds ?? []);
  const [media, setMedia] = useState<SocialMediaItem[]>(source?.media ?? []);
  const [options, setOptions] = useState<PostOptions>(source?.options ?? defaultPostOptions);
  const [commercial, setCommercial] = useState(!!(source?.options.tikTokBrandOrganic || source?.options.tikTokBrandedContent));
  const allowNone = canSeeProject(org, null);
  const [projectId, setProjectId] = useState(source?.projectId ?? defaultProjectId ?? (allowNone ? "" : projects[0]?.id ?? ""));
  const [frequency, setFrequency] = useState<RecurrenceFrequency>(source?.frequency ?? "Daily");
  const [interval, setIntervalText] = useState(String(source?.interval ?? 1));
  const [startDate, setStartDate] = useState(source?.startDate ?? today());
  const [days, setDays] = useState<Weekday[]>(source?.daysOfWeek.length ? source.daysOfWeek : [weekdayOf(source?.startDate ?? today())]);
  const [time, setTime] = useState(source?.timeOfDay ?? "09:00");
  const [endDate, setEndDate] = useState(source?.endDate ?? "");
  // Un post ricorrente creato altrove resta nel suo fuso; uno nuovo prende quello del browser.
  const timeZone = source?.timeZone ?? browserTimeZone();

  const [uploading, setUploading] = useState(0);
  const [busy, setBusy] = useState<"save" | "delete" | "duplicate" | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const usable = accountsFor(accounts, projectId, selected);
  const chosen = accounts.filter((a) => selected.includes(a.id));
  const issues = chosen
    .map((a) => ({ account: a, problems: [...problems(text, media, a.limits), ...optionProblems(a.network, options), ...projectProblems(a, projectId)] }))
    .filter((x) => x.problems.length > 0);
  const incomplete = chosen.some((a) => a.network === "TikTok") && commercialIncomplete(options, commercial);
  const intervalNumber = Number(interval);
  const ruleInvalid = !Number.isInteger(intervalNumber) || intervalNumber < 1 || intervalNumber > 365 || (frequency === "Weekly" && days.length === 0)
    || !time || !startDate || (!!endDate && endDate < startDate);

  /** Un altro progetto: restano scelti solo gli account collegati anche a quello. */
  const changeProject = (id: string) => {
    setProjectId(id);
    if (id) setSelected((s) => s.filter((x) => accounts.find((a) => a.id === x)?.projectIds.includes(id)));
  };

  const toggle = (id: string) => setSelected((s) => (s.includes(id) ? s.filter((x) => x !== id) : [...s, id]));
  const toggleDay = (d: Weekday) => setDays((s) => (s.includes(d) ? s.filter((x) => x !== d) : [...s, d]));

  async function refresh() {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: keys.socialRecurring(org.id) }),
      queryClient.invalidateQueries({ queryKey: keys.socialPosts(org.id) }),
    ]);
  }

  async function save() {
    setBusy("save");
    setError(null);
    const body = {
      text,
      projectId: projectId || null,
      accountIds: selected,
      media: media.map((m) => ({ id: m.id, altText: m.altText || null })),
      options,
      frequency,
      interval: intervalNumber,
      daysOfWeek: frequency === "Weekly" ? days : null,
      timeOfDay: time,
      timeZone,
      startDate,
      endDate: endDate || null,
      isPaused: recurring?.isPaused ?? false, // una copia parte attiva
    };
    try {
      await request(recurring ? `/orgs/${org.id}/social/recurring/${recurring.id}` : `/orgs/${org.id}/social/recurring`, { method: recurring ? "PUT" : "POST", body });
      await refresh();
      onClose();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(null);
    }
  }

  async function duplicate() {
    setBusy("duplicate");
    setError(null);
    try {
      onDuplicate!(await duplicateTemplate(org.id, recurring!));
    } catch (e) {
      setError(e);
      setBusy(null);
    }
  }

  async function remove() {
    setBusy("delete");
    setError(null);
    try {
      await request(`/orgs/${org.id}/social/recurring/${recurring!.id}`, { method: "DELETE" });
      await refresh();
      onClose();
    } catch (e) {
      setError(e);
      setBusy(null);
    }
  }

  const footer = (
    <div className="flex w-full flex-wrap items-center gap-2">
      {recurring && admin && (confirmDelete ? (
        <>
          <Button variant="danger" loading={busy === "delete"} onClick={remove}>Elimina il post ricorrente</Button>
          <Button variant="ghost" onClick={() => setConfirmDelete(false)}>No</Button>
        </>
      ) : <Button variant="ghost" icon={<Trash2 className="size-3.5" />} onClick={() => setConfirmDelete(true)} aria-label="Elimina" />)}
      {recurring && admin && onDuplicate && !confirmDelete && (
        <Button variant="ghost" icon={<Copy className="size-3.5" />} loading={busy === "duplicate"} onClick={duplicate}
          title="Un post ricorrente nuovo con gli stessi valori: cambi solo quello che serve">Duplica</Button>
      )}
      <span className="flex-1" />
      {readOnly ? <Button variant="primary" onClick={onClose}>Chiudi</Button> : (
        <>
          <Button variant="ghost" onClick={onClose}>Annulla</Button>
          <Button variant="primary" loading={busy === "save"} disabled={uploading > 0 || selected.length === 0 || issues.length > 0 || incomplete || ruleInvalid || (!allowNone && !projectId)} onClick={save}>
            {recurring ? "Salva" : "Crea"}
          </Button>
        </>
      )}
    </div>
  );

  const rule = { frequency, interval: intervalNumber || 1, daysOfWeek: days, timeOfDay: time, timeZone, startDate, endDate: endDate || null };

  return (
    <Modal open onOpenChange={(o) => !o && onClose()} wide footer={footer}
      title={recurring ? (readOnly ? "Post ricorrente" : "Modifica post ricorrente") : template ? "Copia di un post ricorrente" : "Nuovo post ricorrente"}
      description={template
        ? "Un post ricorrente nuovo, con gli stessi valori e una copia delle immagini: cambia testo, foto o regola e crealo. L'originale non cambia."
        : "Esce da solo all'ora indicata, ogni volta con questo contenuto. Le modifiche valgono per tutte le uscite future; quelle già uscite restano sul calendario."}>
      <div className="space-y-5">
        <ProjectField projects={projects} value={projectId} onChange={changeProject} allowNone={allowNone} readOnly={readOnly} />

        <Field label="Dove">
          <AccountPicker accounts={usable} selected={selected} onToggle={toggle} readOnly={readOnly} inProject={!!projectId} />
        </Field>

        <div className="space-y-2">
          <Field label="Testo">
            <Textarea rows={5} readOnly={readOnly} className="font-sans text-[0.8125rem]" value={text} onChange={(e) => setText(e.target.value)}
              placeholder="Lo stesso testo a ogni uscita. Link e #hashtag funzionano su tutte le reti." />
          </Field>
          {chosen.length > 0 && (
            <ul className="space-y-1">
              {chosen.map((a) => {
                const count = countCharacters(text, a.limits);
                const accountIssues = issues.find((x) => x.account.id === a.id)?.problems ?? [];
                return (
                  <li key={a.id} className="flex flex-wrap items-center gap-x-2 gap-y-1 rounded-md border border-line px-2.5 py-1.5 text-xs">
                    <NetworkGlyph network={a.network} className="size-3.5 text-muted" />
                    <span className="text-fg">{accountLabel(a)}</span>
                    <span className={clsx("font-mono", count > a.limits.maxCharacters ? "text-bad" : "text-faint")}>{count}/{a.limits.maxCharacters}</span>
                    {accountIssues.length > 0 && <span className="text-bad">{accountIssues.join(" · ")}</span>}
                  </li>
                );
              })}
            </ul>
          )}
        </div>

        <Field label="Immagini o video" hint={!readOnly ? mediaHint : undefined}>
          <MediaField media={media} onChange={setMedia} readOnly={readOnly} onUploading={setUploading} onError={setError} />
        </Field>

        <NetworkOptions accounts={chosen} media={media} options={options} onChange={setOptions} commercial={commercial} onCommercial={setCommercial} readOnly={readOnly} />

        <fieldset disabled={readOnly} className="space-y-3 rounded-md border border-line p-3">
          <legend className="px-1 text-xs font-medium text-muted">Quando</legend>
          <Segmented value={frequency} onChange={setFrequency}
            options={[{ value: "Daily", label: "Giornaliero" }, { value: "Weekly", label: "Settimanale" }, { value: "Monthly", label: "Mensile" }]} />

          <div className="grid gap-3 sm:grid-cols-3">
            <Field label={frequency === "Daily" ? "Ogni quanti giorni" : frequency === "Weekly" ? "Ogni quante settimane" : "Ogni quanti mesi"}>
              <Input type="number" min={1} max={365} value={interval} onChange={(e) => setIntervalText(e.target.value)} />
            </Field>
            <Field label="Ora"><Input type="time" value={time} onChange={(e) => setTime(e.target.value)} /></Field>
            <Field label={frequency === "Monthly" ? "Dal (dà il giorno del mese)" : "Dal"}>
              <Input type="date" value={startDate} onChange={(e) => setStartDate(e.target.value)} />
            </Field>
          </div>

          {frequency === "Weekly" && (
            <Field label="Giorni">
              <div className="flex flex-wrap gap-1.5">
                {weekdays.map((d) => (
                  <button key={d.value} type="button" aria-pressed={days.includes(d.value)} title={d.long} onClick={() => toggleDay(d.value)}
                    className={clsx(
                      "size-8 rounded-md border text-[0.8125rem] transition-colors disabled:cursor-not-allowed",
                      days.includes(d.value) ? "border-brand/60 bg-brand/10 text-fg" : "border-line-strong bg-panel-2 text-muted hover:text-fg",
                    )}>
                    {d.short}
                  </button>
                ))}
              </div>
            </Field>
          )}

          <Field label="Fino al (facoltativo)" hint="Vuoto: senza fine. Il giorno indicato è compreso.">
            <Input type="date" value={endDate} min={startDate} onChange={(e) => setEndDate(e.target.value)} className="sm:w-56" />
          </Field>

          {!ruleInvalid && <p className="text-[0.8125rem] text-fg">{describeRule(rule)}.</p>}
          {frequency === "Monthly" && Number(startDate.slice(8, 10)) > 28 && (
            <p className="text-xs text-muted">Nei mesi che non arrivano a quel giorno esce l'ultimo giorno del mese.</p>
          )}
          {recurring && recurring.upcoming.length > 0 && (
            <p className="text-xs text-muted">Prossime uscite (con la regola salvata): {recurring.upcoming.map((d) => formatDateTime(d)).join(" · ")}</p>
          )}
        </fieldset>

        {error ? <Alert tone="bad">{errorMessage(error)}</Alert> : null}
      </div>
    </Modal>
  );
}
