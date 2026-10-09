import { useQueryClient } from "@tanstack/react-query";
import clsx from "clsx";
import { ImagePlus, Lock, RefreshCw, Trash2 } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { errorMessage, request } from "../../api/client";
import { canAdmin, keys, useListing, useScreenshots } from "../../api/hooks";
import type { AppleListing, AppleLocaleText, GoogleListing, GoogleLocaleText, Project, Store } from "../../api/types";
import { useOrg } from "../../components/org";
import { StoreGlyph, storeName } from "../../components/StoreIcons";
import { Alert, Button, EmptyState, Field, Input, PageLoader, Panel, Segmented, Select, Spinner, Textarea } from "../../components/ui";

const APPLE_SCREENS: Record<string, string> = {
  APP_IPHONE_67: "iPhone 6,7\"", APP_IPHONE_65: "iPhone 6,5\"", APP_IPHONE_61: "iPhone 6,1\"", APP_IPHONE_55: "iPhone 5,5\"",
  APP_IPAD_PRO_3GEN_129: "iPad Pro 12,9\"", APP_IPAD_PRO_129: "iPad Pro 12,9\" (2ª gen)", APP_IPAD_PRO_3GEN_11: "iPad Pro 11\"",
};
const GOOGLE_IMAGES: Record<string, string> = {
  phoneScreenshots: "Telefono", sevenInchScreenshots: "Tablet 7\"", tenInchScreenshots: "Tablet 10\"", featureGraphic: "Grafica in evidenza", icon: "Icona",
};

/** Testi e screenshot della pagina dello store, uno store alla volta. */
export function ListingTab({ project }: { project: Project }) {
  const org = useOrg();
  const listing = useListing(org.id, project.id);
  const queryClient = useQueryClient();
  const stores = project.apps.map((a) => a.store);
  const [store, setStore] = useState<Store>(stores[0]);

  if (stores.length === 0) return <EmptyState title="Nessuna app collegata">Collega un'app dalle Impostazioni del progetto per gestirne la pagina sullo store.</EmptyState>;
  if (listing.isPending) return <PageLoader />;

  const result = store === "AppStore" ? listing.data?.appStore : listing.data?.googlePlay;

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        {stores.length > 1 ? (
          <Segmented value={store} onChange={setStore}
            options={stores.map((s) => ({ value: s, label: <><StoreGlyph store={s} className="size-3.5" />{storeName(s)}</> }))} />
        ) : <span />}
        <Button size="sm" variant="ghost" icon={<RefreshCw className="size-3" />} loading={listing.isFetching}
          onClick={() => queryClient.invalidateQueries({ queryKey: keys.listing(org.id, project.id) })}>
          Ricarica dallo store
        </Button>
      </div>

      {result?.error && <Alert tone="warn">{result.error}</Alert>}
      {result?.data && (store === "AppStore"
        ? <AppleEditor project={project} listing={result.data as AppleListing} />
        : <GoogleEditor project={project} listing={result.data as GoogleListing} />)}
    </div>
  );
}

/** Un campo con il contatore: il limite è quello dello store. */
function Counted({ label, value, onChange, max, rows, disabled, hint }: {
  label: string; value: string; onChange: (v: string) => void; max: number; rows?: number; disabled?: boolean; hint?: string;
}) {
  const over = value.length > max;
  return (
    <Field label={label} hint={<span className={clsx(over && "text-bad")}>{value.length}/{max}{hint ? ` · ${hint}` : ""}</span>}>
      {rows ? (
        <Textarea rows={rows} className="font-sans text-[0.8125rem]" value={value} onChange={(e) => onChange(e.target.value)} disabled={disabled} />
      ) : (
        <Input value={value} onChange={(e) => onChange(e.target.value)} disabled={disabled} />
      )}
    </Field>
  );
}

function useSave<T>(path: string, invalidate: readonly unknown[]) {
  const queryClient = useQueryClient();
  const [state, setState] = useState<{ busy: boolean; error: unknown; saved: boolean }>({ busy: false, error: null, saved: false });
  const save = async (body: T) => {
    setState({ busy: true, error: null, saved: false });
    try {
      await request(path, { method: "PUT", body });
      await queryClient.invalidateQueries({ queryKey: invalidate });
      setState({ busy: false, error: null, saved: true });
    } catch (error) {
      setState({ busy: false, error, saved: false });
    }
  };
  return { ...state, save, reset: () => setState({ busy: false, error: null, saved: false }) };
}

function AppleEditor({ project, listing }: { project: Project; listing: AppleListing }) {
  const org = useOrg();
  const admin = canAdmin(org.role);
  const [locale, setLocale] = useState(listing.locales[0]?.locale ?? "");
  const current = listing.locales.find((l) => l.locale === locale);
  const [form, setForm] = useState<AppleLocaleText | undefined>(current);
  useEffect(() => setForm(current), [current]);
  const save = useSave<Partial<AppleLocaleText>>(`/orgs/${org.id}/projects/${project.id}/listing/app-store/${encodeURIComponent(locale)}`, keys.listing(org.id, project.id));

  if (!form) return <EmptyState title="Nessuna lingua">La scheda App Store non ha ancora testi.</EmptyState>;
  const set = (key: keyof AppleLocaleText) => (v: string) => { save.reset(); setForm({ ...form, [key]: v }); };
  const v = (key: keyof AppleLocaleText) => (form[key] as string | null) ?? "";

  return (
    <>
      <Panel
        title={<span className="flex items-center gap-2">Testi {listing.version && <span className="font-mono text-xs text-muted">versione {listing.version}</span>}</span>}
        actions={<Select className="w-40" value={locale} onChange={(e) => setLocale(e.target.value)}>{listing.locales.map((l) => <option key={l.locale} value={l.locale}>{l.locale}</option>)}</Select>}
        footer={admin && <Button variant="primary" loading={save.busy} onClick={() => save.save(form)}>Salva su App Store</Button>}
      >
        <div className="space-y-4 p-4">
          {!listing.versionEditable && (
            <Alert tone="info" title="Versione già pubblicata">
              Descrizione, parole chiave e novità si cambiano su una nuova versione: creala da App Store Connect e tornerà modificabile qui. Il testo promozionale si può cambiare sempre.
            </Alert>
          )}
          <div className="grid gap-4 sm:grid-cols-2">
            <Counted label="Nome" value={v("name")} onChange={set("name")} max={30} disabled={!admin || !listing.infoEditable} hint={listing.infoEditable ? undefined : "bloccato finché la scheda non è in preparazione"} />
            <Counted label="Sottotitolo" value={v("subtitle")} onChange={set("subtitle")} max={30} disabled={!admin || !listing.infoEditable} />
          </div>
          <Counted label="Testo promozionale" value={v("promotionalText")} onChange={set("promotionalText")} max={170} rows={2} disabled={!admin} hint="modificabile sempre, senza revisione" />
          <Counted label="Descrizione" value={v("description")} onChange={set("description")} max={4000} rows={8} disabled={!admin || !listing.versionEditable} />
          <Counted label="Parole chiave" value={v("keywords")} onChange={set("keywords")} max={100} disabled={!admin || !listing.versionEditable} hint="separate da virgole" />
          <Counted label="Novità di questa versione" value={v("whatsNew")} onChange={set("whatsNew")} max={4000} rows={4} disabled={!admin || !listing.versionEditable} />
          <div className="grid gap-4 sm:grid-cols-3">
            <Field label="URL di supporto"><Input value={v("supportUrl")} onChange={(e) => set("supportUrl")(e.target.value)} disabled={!admin || !listing.versionEditable} /></Field>
            <Field label="URL marketing"><Input value={v("marketingUrl")} onChange={(e) => set("marketingUrl")(e.target.value)} disabled={!admin || !listing.versionEditable} /></Field>
            <Field label="Privacy policy"><Input value={v("privacyPolicyUrl")} onChange={(e) => set("privacyPolicyUrl")(e.target.value)} disabled={!admin || !listing.infoEditable} /></Field>
          </div>
          {save.error ? <Alert tone="bad">{errorMessage(save.error)}</Alert> : null}
          {save.saved && <Alert tone="ok">Salvato su App Store Connect.</Alert>}
        </div>
      </Panel>
      <ScreenshotPanel project={project} store="AppStore" locale={locale} labels={APPLE_SCREENS} editable={admin && listing.versionEditable}
        lockedReason="Gli screenshot si cambiano su una versione in preparazione." />
    </>
  );
}

function GoogleEditor({ project, listing }: { project: Project; listing: GoogleListing }) {
  const org = useOrg();
  const admin = canAdmin(org.role);
  const [language, setLanguage] = useState(listing.defaultLanguage ?? listing.locales[0]?.language ?? "");
  const current = listing.locales.find((l) => l.language === language);
  const [form, setForm] = useState<GoogleLocaleText | undefined>(current);
  useEffect(() => setForm(current), [current]);
  const save = useSave<Partial<GoogleLocaleText>>(`/orgs/${org.id}/projects/${project.id}/listing/google-play/${encodeURIComponent(language)}`, keys.listing(org.id, project.id));

  if (!form) return <EmptyState title="Nessuna lingua">La scheda Google Play non ha ancora testi.</EmptyState>;
  const set = (key: keyof GoogleLocaleText) => (v: string) => { save.reset(); setForm({ ...form, [key]: v }); };

  return (
    <>
      <Panel
        title="Testi"
        actions={<Select className="w-40" value={language} onChange={(e) => setLanguage(e.target.value)}>{listing.locales.map((l) => <option key={l.language} value={l.language}>{l.language}{l.language === listing.defaultLanguage ? " (predefinita)" : ""}</option>)}</Select>}
        footer={admin && <Button variant="primary" loading={save.busy} onClick={() => save.save(form)}>Salva su Google Play</Button>}
      >
        <div className="space-y-4 p-4">
          <Alert tone="info">Le modifiche alla scheda vanno in revisione da Google prima di comparire sullo store.</Alert>
          <Counted label="Titolo" value={form.title} onChange={set("title")} max={30} disabled={!admin} />
          <Counted label="Descrizione breve" value={form.shortDescription} onChange={set("shortDescription")} max={80} disabled={!admin} />
          <Counted label="Descrizione completa" value={form.fullDescription} onChange={set("fullDescription")} max={4000} rows={10} disabled={!admin} />
          <Field label="Video YouTube (facoltativo)"><Input value={form.video ?? ""} onChange={(e) => set("video")(e.target.value)} disabled={!admin} /></Field>
          {save.error ? <Alert tone="bad">{errorMessage(save.error)}</Alert> : null}
          {save.saved && <Alert tone="ok">Salvato su Google Play: la modifica è in revisione.</Alert>}
        </div>
      </Panel>
      <ScreenshotPanel project={project} store="GooglePlay" locale={language} labels={GOOGLE_IMAGES} editable={admin} />
    </>
  );
}

/** Gli screenshot di una lingua, per tipo di schermo, con caricamento ed eliminazione. */
function ScreenshotPanel({ project, store, locale, labels, editable, lockedReason }: {
  project: Project; store: Store; locale: string; labels: Record<string, string>; editable: boolean; lockedReason?: string;
}) {
  const org = useOrg();
  const queryClient = useQueryClient();
  const shots = useScreenshots(org.id, project.id, store, locale);
  const [group, setGroup] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const input = useRef<HTMLInputElement>(null);

  const groups = Array.from(new Set([...(shots.data ?? []).map((g) => g.group), ...Object.keys(labels)]));
  const selected = group ?? (shots.data?.find((g) => g.images.length > 0)?.group ?? groups[0]);
  const images = shots.data?.find((g) => g.group === selected)?.images ?? [];
  const refresh = () => queryClient.invalidateQueries({ queryKey: keys.screenshots(org.id, project.id, store, locale) });

  async function upload(files: FileList) {
    setError(null);
    try {
      for (const file of Array.from(files)) {
        setBusy(file.name);
        const form = new FormData();
        form.append("store", store);
        form.append("locale", locale);
        form.append("group", selected);
        form.append("file", file);
        await request(`/orgs/${org.id}/projects/${project.id}/listing/screenshots`, { method: "POST", body: form });
      }
      await refresh();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(null);
    }
  }

  async function remove(id: string) {
    setError(null);
    setBusy(id);
    try {
      const q = new URLSearchParams({ store, locale, group: selected, id });
      await request(`/orgs/${org.id}/projects/${project.id}/listing/screenshots?${q}`, { method: "DELETE" });
      await refresh();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(null);
    }
  }

  return (
    <Panel
      title="Immagini"
      description={`Lingua ${locale}`}
      actions={
        <>
          <Select className="w-48" value={selected} onChange={(e) => setGroup(e.target.value)}>
            {groups.map((g) => <option key={g} value={g}>{labels[g] ?? g}</option>)}
          </Select>
          {editable && (
            <Button size="sm" icon={busy && !images.some((i) => i.id === busy) ? <Spinner className="size-3" /> : <ImagePlus className="size-3.5" />}
              disabled={!!busy} onClick={() => input.current?.click()}>
              Aggiungi
            </Button>
          )}
          <input ref={input} type="file" accept="image/png,image/jpeg" multiple className="hidden"
            onChange={(e) => { if (e.target.files?.length) upload(e.target.files); e.target.value = ""; }} />
        </>
      }
    >
      <div className="space-y-3 p-4">
        {!editable && lockedReason && <p className="flex items-center gap-1.5 text-xs text-faint"><Lock className="size-3" />{lockedReason}</p>}
        {error ? <Alert tone="bad">{errorMessage(error)}</Alert> : null}
        {shots.isPending ? <PageLoader /> : shots.error ? <Alert tone="warn">{errorMessage(shots.error)}</Alert> : images.length === 0 ? (
          <p className="py-8 text-center text-xs text-muted">Nessuna immagine per {labels[selected] ?? selected}.</p>
        ) : (
          <div className="flex gap-3 overflow-x-auto pb-2">
            {images.map((img) => (
              <figure key={img.id} className="group relative shrink-0">
                <img src={img.url} alt={img.fileName ?? ""} className="h-72 rounded-md border border-line object-contain" loading="lazy" />
                {editable && (
                  <button type="button" aria-label="Elimina" disabled={!!busy} onClick={() => remove(img.id)}
                    className="absolute top-2 right-2 rounded-md border border-line-strong bg-panel/90 p-1.5 text-muted opacity-0 transition-opacity group-hover:opacity-100 hover:text-bad">
                    {busy === img.id ? <Spinner className="size-3.5" /> : <Trash2 className="size-3.5" />}
                  </button>
                )}
              </figure>
            ))}
          </div>
        )}
      </div>
    </Panel>
  );
}
