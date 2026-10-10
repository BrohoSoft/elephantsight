import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Archive, Download, Play, Trash2 } from "lucide-react";
import { useEffect, useState } from "react";
import { errorMessage, request } from "../api/client";
import type { BackupStatus, SettingsGroup } from "../api/types";
import { formatDateTime } from "../components/org";
import { Alert, Badge, Button, Field, Input, Mono, Panel, Select } from "../components/ui";

const backupsKey = ["instance", "backups"] as const;

const formatSize = (bytes: number) =>
  bytes > 1024 * 1024 * 1024 ? `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB` : bytes > 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.round(bytes / 1024)} KB`;

const browserZone = () => Intl.DateTimeFormat().resolvedOptions().timeZone;

/**
 * I backup dell'istanza: quando e ogni quanto, quanti tenerne, la password che
 * li cifra; lo storico, "fai un backup ora", scaricare e cancellare i file.
 * Il ripristino non c'è: si fa da riga di comando, a istanza ferma. La pagina
 * non compare se chi ha installato ha spento la funzione (BACKUPS_ENABLED=false).
 */
export function BackupSettings({ onSaved }: { onSaved: (groups: SettingsGroup[]) => void }) {
  const queryClient = useQueryClient();
  // Dopo "Fai un backup ora": l'ultimo backup che c'era prima. Si aggiorna
  // finché non ne compare uno nuovo finito (uno piccolo può finire prima del
  // primo aggiornamento, e "running" allora non si vede mai).
  const [awaitingAfter, setAwaitingAfter] = useState<string | null | undefined>(undefined);
  const status = useQuery({
    queryKey: backupsKey,
    queryFn: () => request<BackupStatus>("/instance/backups"),
    refetchInterval: (q) => (q.state.data?.running || awaitingAfter !== undefined ? 1500 : false),
  });
  const latest = status.data?.runs[0];
  useEffect(() => {
    if (awaitingAfter !== undefined && latest && latest.id !== awaitingAfter && latest.finishedAtUtc) setAwaitingAfter(undefined);
  }, [awaitingAfter, latest]);
  const [changes, setChanges] = useState<Record<string, string>>({});
  const [confirm, setConfirm] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);

  if (status.isPending) return null;
  if (status.error) return <Alert tone="bad">{errorMessage(status.error)}</Alert>;
  const s = status.data!;

  const value = (name: string, current: string) => changes[name] ?? current;
  const set = (name: string, v: string) => setChanges((c) => ({ ...c, [name]: v }));
  const active = value("active", String(s.active)) === "true";
  const passwordMismatch = !!changes.password && changes.password !== confirm;

  async function act(name: string, action: () => Promise<unknown>) {
    setBusy(name);
    setError(null);
    setSaved(false);
    try {
      await action();
      await queryClient.invalidateQueries({ queryKey: backupsKey });
    } catch (e) {
      setError(e);
    } finally {
      setBusy(null);
    }
  }

  const save = () =>
    act("save", async () => {
      // Il fuso, se non è mai stato scelto, è quello di chi programma.
      const body = { ...changes, ...(s.timeZone || changes.timeZone ? {} : { timeZone: browserZone() }) };
      onSaved(await request<SettingsGroup[]>("/instance/settings/backup", { method: "PUT", body }));
      setChanges({});
      setConfirm("");
      setSaved(true);
    });

  const download = (name: string) =>
    act("download:" + name, async () => {
      const { url } = await request<{ url: string }>(`/instance/backups/files/${name}/link`, { method: "POST" });
      // Un link normale: il file può essere grande, non passa dalla memoria della pagina.
      const link = document.createElement("a");
      link.href = url;
      link.download = name;
      link.click();
    });

  return (
    <Panel title={<span className="flex items-center gap-2"><Archive className="size-4" /> Backup</span>}
      description={<>
        Database, chiavi, credenziali cifrate e report degli store, in un file cifrato con la password scelta qui. Stanno sul disco del server
        (<Mono>{s.directory}</Mono>): scaricali per averne una copia altrove, se si perde la macchina si perdono anche loro. I file dei post non ci sono.
        Il ripristino si fa da riga di comando, a istanza ferma (vedi README).
      </>}
      footer={
        <>
          <Button icon={<Play className="size-3.5" />} loading={busy === "run" || s.running || awaitingAfter !== undefined} disabled={!s.passwordSet || s.running || awaitingAfter !== undefined}
            title={s.passwordSet ? undefined : "Imposta prima la password"}
            onClick={() => act("run", async () => {
              const before = s.runs[0]?.id ?? null;
              await request("/instance/backups/run", { method: "POST" });
              setAwaitingAfter(before);
            })}>
            {s.running || awaitingAfter !== undefined ? "Backup in corso…" : "Fai un backup ora"}
          </Button>
          <span className="flex-1" />
          <Button variant="primary" loading={busy === "save"} disabled={Object.keys(changes).length === 0 || passwordMismatch} onClick={save}>Salva</Button>
        </>
      }>
      <div className="grid gap-4 p-4 sm:grid-cols-2">
        <Field label="Backup programmati" hint={s.nextRunUtc && s.active ? `Prossimo: ${formatDateTime(s.nextRunUtc)}` : undefined}>
          <Select value={active ? "true" : "false"} onChange={(e) => set("active", e.target.value)}>
            <option value="false">Spenti</option>
            <option value="true">Attivi</option>
          </Select>
        </Field>
        <Field label="Ogni quanto">
          <Select value={value("everyDays", String(s.everyDays))} onChange={(e) => set("everyDays", e.target.value)}>
            {[1, 2, 3, 7, 14, 30].map((d) => <option key={d} value={d}>{d === 1 ? "Ogni giorno" : d === 7 ? "Ogni settimana" : `Ogni ${d} giorni`}</option>)}
          </Select>
        </Field>
        <Field label="A che ora" hint={`Fuso: ${value("timeZone", s.timeZone || browserZone())}`}>
          <Input type="time" value={value("time", s.time)} onChange={(e) => set("time", e.target.value)} />
        </Field>
        <Field label="Quanti tenerne" hint="I più vecchi si cancellano dopo ogni backup riuscito.">
          <Input type="number" min={1} max={365} value={value("keep", String(s.keep))} onChange={(e) => set("keep", e.target.value)} />
        </Field>
        <Field label="Password dei backup" hint={s.passwordSet ? <Badge tone="brand">Impostata</Badge> : <Badge tone="warn">Non impostata</Badge>}>
          <Input type="password" autoComplete="new-password" className="font-mono" value={changes.password ?? ""}
            placeholder={s.passwordSet ? "•••••••• (scrivi per sostituirla)" : "Almeno 12 caratteri"} onChange={(e) => set("password", e.target.value)} />
        </Field>
        {changes.password ? (
          <Field label="Ripeti la password" error={passwordMismatch && confirm ? "Le due password non coincidono." : undefined}>
            <Input type="password" autoComplete="new-password" className="font-mono" value={confirm} onChange={(e) => setConfirm(e.target.value)} />
          </Field>
        ) : <div />}
        <p className="text-xs text-muted sm:col-span-2">
          Scrivi la password in un posto sicuro, fuori da questo server: senza, i backup non si aprono, e nessuno può recuperarla.
          Cambiandola, i backup già fatti restano con quella vecchia.
        </p>
        {error ? <div className="sm:col-span-2"><Alert tone="bad">{errorMessage(error)}</Alert></div> : null}
        {saved && <div className="sm:col-span-2"><Alert tone="ok">Salvato: vale da subito.</Alert></div>}
      </div>

      {s.files.length > 0 && (
        <div className="border-t border-line">
          <p className="px-4 pt-3 text-xs font-medium text-muted">File sul server</p>
          <ul className="divide-y divide-line">
            {s.files.map((f) => (
              <li key={f.name} className="flex items-center gap-3 px-4 py-2">
                <span className="min-w-0 flex-1 truncate text-[0.8125rem] text-fg">{formatDateTime(f.createdAtUtc)} <span className="text-xs text-muted">· {formatSize(f.sizeBytes)} · {f.name}</span></span>
                <Button size="sm" variant="ghost" icon={<Download className="size-3" />} loading={busy === "download:" + f.name} onClick={() => download(f.name)}>Scarica</Button>
                <Button size="sm" variant="ghost" icon={<Trash2 className="size-3" />} aria-label={`Cancella ${f.name}`}
                  onClick={() => window.confirm(`Cancellare il backup del ${formatDateTime(f.createdAtUtc)}?`) &&
                    act("delete", () => request(`/instance/backups/files/${f.name}`, { method: "DELETE" }))} />
              </li>
            ))}
          </ul>
        </div>
      )}

      {s.runs.length > 0 && (
        <div className="border-t border-line pb-2">
          <p className="px-4 pt-3 text-xs font-medium text-muted">Ultimi backup</p>
          <ul className="divide-y divide-line">
            {s.runs.slice(0, 10).map((r) => (
              <li key={r.id} className="px-4 py-2 text-[0.8125rem]">
                <span className="flex items-center gap-2">
                  {r.finishedAtUtc === null ? <Badge>In corso</Badge> : r.error ? <Badge tone="bad">Non riuscito</Badge> : <Badge tone="ok">Fatto</Badge>}
                  <span className="text-fg">{formatDateTime(r.startedAtUtc)}</span>
                  <span className="text-xs text-muted">{r.manual ? "a mano" : "programmato"}{r.sizeBytes ? ` · ${formatSize(r.sizeBytes)}` : ""}</span>
                </span>
                {r.error && <p className="mt-1 text-xs text-bad">{r.error}</p>}
              </li>
            ))}
          </ul>
        </div>
      )}
    </Panel>
  );
}
