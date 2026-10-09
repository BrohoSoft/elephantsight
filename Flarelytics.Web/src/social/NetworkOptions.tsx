import { useQueries } from "@tanstack/react-query";
import { errorMessage, request } from "../api/client";
import type { PostOptions, SocialAccount, SocialMediaItem, TikTokCreator, TikTokPrivacy } from "../api/types";
import { useOrg } from "../components/org";
import { NetworkGlyph } from "../components/SocialIcons";
import { Alert, Field, Select, Spinner } from "../components/ui";

const privacyLabel: Record<TikTokPrivacy, string> = {
  PUBLIC_TO_EVERYONE: "Tutti",
  MUTUAL_FOLLOW_FRIENDS: "Amici (follower che segui)",
  FOLLOWER_OF_CREATOR: "Follower",
  SELF_ONLY: "Solo io",
};

const checkbox = "size-3.5 accent-brand disabled:opacity-50";

/**
 * Le scelte che dipendono dalla rete: per i Reel la griglia del profilo, per
 * TikTok quello che TikTok stesso impone di mostrare prima di pubblicare
 * (account, visibilità senza scelta predefinita, interazioni spente di default,
 * contenuti commerciali, consenso sulla musica).
 */
export function NetworkOptions({ accounts, media, options, onChange, commercial, onCommercial, readOnly }: {
  accounts: SocialAccount[];
  media: SocialMediaItem[];
  options: PostOptions;
  onChange: (options: PostOptions) => void;
  /** "Contenuto commerciale" acceso: sta fuori dalle opzioni perché acceso senza scelte non si può salvare. */
  commercial: boolean;
  onCommercial: (on: boolean) => void;
  readOnly: boolean;
}) {
  const hasVideo = media.some((m) => m.kind === "Video");
  const instagram = accounts.some((a) => a.network === "Instagram");
  const tiktok = accounts.filter((a) => a.network === "TikTok");
  const set = (patch: Partial<PostOptions>) => onChange({ ...options, ...patch });

  if (!(instagram && hasVideo) && tiktok.length === 0) return null;

  return (
    <div className="space-y-4">
      {instagram && hasVideo && (
        <div className="rounded-md border border-line px-3 py-2.5">
          <p className="mb-1.5 flex items-center gap-1.5 text-xs font-medium text-fg"><NetworkGlyph network="Instagram" className="size-3.5" /> Reel di Instagram</p>
          <label className="flex items-center gap-2 text-[0.8125rem] text-fg">
            <input type="checkbox" className={checkbox} disabled={readOnly} checked={options.instagramShowInGrid}
              onChange={(e) => set({ instagramShowInGrid: e.target.checked })} />
            Mostra nella griglia del profilo
          </label>
          <p className="mt-1 text-xs text-muted">Se lo togli, il Reel compare solo nella scheda Reel del profilo.</p>
        </div>
      )}
      {tiktok.length > 0 && <TikTokOptions accounts={tiktok} media={media} options={options} set={set} commercial={commercial} onCommercial={onCommercial} readOnly={readOnly} />}
    </div>
  );
}

function TikTokOptions({ accounts, media, options, set, commercial, onCommercial, readOnly }: {
  accounts: SocialAccount[];
  media: SocialMediaItem[];
  options: PostOptions;
  set: (patch: Partial<PostOptions>) => void;
  commercial: boolean;
  onCommercial: (on: boolean) => void;
  readOnly: boolean;
}) {
  const org = useOrg();
  // Le informazioni del creator, fresche ogni volta che si apre: lo chiede TikTok.
  const creators = useQueries({
    queries: accounts.map((a) => ({
      queryKey: ["org", org.id, "social", "tiktok-creator", a.id],
      queryFn: () => request<TikTokCreator>(`/orgs/${org.id}/social/accounts/${a.id}/tiktok-creator`),
      staleTime: 0,
      retry: false,
    })),
  });

  if (creators.some((c) => c.isPending)) return <div className="flex items-center gap-2 text-xs text-muted"><Spinner /> Informazioni dell'account TikTok…</div>;
  const failed = creators.find((c) => c.error);
  if (failed) return <Alert tone="bad" title="TikTok non risponde">{errorMessage(failed.error)} Riprova più tardi.</Alert>;

  const infos = creators.map((c) => c.data!);
  // Con più account TikTok valgono le scelte permesse a tutti.
  const levels = infos.reduce<TikTokPrivacy[]>((acc, c) => acc.filter((l) => c.privacyLevels.includes(l)), [...infos[0].privacyLevels]);
  const commentOff = infos.some((c) => c.commentDisabled);
  const duetOff = infos.some((c) => c.duetDisabled);
  const stitchOff = infos.some((c) => c.stitchDisabled);
  const maxSeconds = Math.min(...infos.map((c) => c.maxVideoSeconds));
  const video = media.find((m) => m.kind === "Video");
  const tooLong = video && (video.durationMs ?? 0) / 1000 > maxSeconds;

  return (
    <div className="space-y-3 rounded-md border border-line px-3 py-2.5">
      <p className="flex items-center gap-1.5 text-xs font-medium text-fg">
        <NetworkGlyph network="TikTok" className="size-3.5" /> TikTok · {infos.map((c) => `${c.nickname} (@${c.username})`).join(", ")}
      </p>
      {tooLong && <Alert tone="bad">Il video supera la durata che questo account può pubblicare ({Math.floor(maxSeconds / 60)} minuti).</Alert>}

      <Field label="Chi può vedere questo video">
        <Select disabled={readOnly} value={options.tikTokPrivacy ?? ""} onChange={(e) => set({ tikTokPrivacy: (e.target.value || null) as TikTokPrivacy | null })}>
          {/* TikTok vuole che la scelta la faccia la persona: nessun valore predefinito. */}
          <option value="" disabled>Scegli…</option>
          {levels.map((l) => (
            <option key={l} value={l} disabled={l === "SELF_ONLY" && options.tikTokBrandedContent}>{privacyLabel[l]}</option>
          ))}
        </Select>
      </Field>
      {levels.length === 1 && levels[0] === "SELF_ONLY" && (
        <p className="text-xs text-muted">Finché l'app TikTok non ha passato la revisione si può pubblicare solo con visibilità "Solo io", su account privati.</p>
      )}

      <div className="space-y-1">
        <p className="text-xs font-medium text-muted">Consenti agli utenti di</p>
        <div className="flex flex-wrap gap-x-4 gap-y-1 text-[0.8125rem] text-fg">
          {([["tikTokAllowComment", "Commentare", commentOff], ["tikTokAllowDuet", "Fare un duetto", duetOff], ["tikTokAllowStitch", "Fare uno stitch", stitchOff]] as const).map(([key, label, off]) => (
            <label key={key} className="flex items-center gap-1.5" title={off ? "Disattivato nelle impostazioni dell'account TikTok" : undefined}>
              <input type="checkbox" className={checkbox} disabled={readOnly || off} checked={!off && options[key]} onChange={(e) => set({ [key]: e.target.checked })} />
              <span className={off ? "text-faint" : undefined}>{label}</span>
            </label>
          ))}
        </div>
      </div>

      <div className="space-y-1.5">
        <label className="flex items-center gap-2 text-[0.8125rem] text-fg">
          <input type="checkbox" className={checkbox} disabled={readOnly} checked={commercial}
            onChange={(e) => {
              // Acceso: nessuna delle due preselezionata, come vuole TikTok. Spento: si tolgono tutte e due.
              onCommercial(e.target.checked);
              if (!e.target.checked) set({ tikTokBrandOrganic: false, tikTokBrandedContent: false });
            }} />
          Contenuto commerciale
        </label>
        {commercial && (
          <div className="space-y-1 pl-6 text-[0.8125rem] text-fg">
            <label className="flex items-center gap-2">
              <input type="checkbox" className={checkbox} disabled={readOnly} checked={options.tikTokBrandOrganic} onChange={(e) => set({ tikTokBrandOrganic: e.target.checked })} />
              Il tuo marchio <span className="text-xs text-muted">· etichetta "Contenuto promozionale"</span>
            </label>
            <label className="flex items-center gap-2" title={options.tikTokPrivacy === "SELF_ONLY" ? "Una partnership retribuita non può essere privata" : undefined}>
              <input type="checkbox" className={checkbox} disabled={readOnly || options.tikTokPrivacy === "SELF_ONLY"} checked={options.tikTokBrandedContent}
                onChange={(e) => set({ tikTokBrandedContent: e.target.checked })} />
              Contenuto di marca <span className="text-xs text-muted">· etichetta "Partnership retribuita"</span>
            </label>
            {!options.tikTokBrandOrganic && !options.tikTokBrandedContent && <p className="text-xs text-bad">Scegli almeno una delle due, o togli "Contenuto commerciale".</p>}
          </div>
        )}
      </div>

      <p className="text-xs text-muted">
        {options.tikTokBrandedContent
          ? "Pubblicando accetti la Branded Content Policy e la Music Usage Confirmation di TikTok."
          : "Pubblicando accetti la Music Usage Confirmation di TikTok."}
        {" "}Dopo l'invio TikTok può impiegare qualche minuto per elaborare il video.
      </p>
    </div>
  );
}

/** "Contenuto commerciale" acceso vuole almeno una delle due caselle: fino ad allora non si pubblica. */
export const commercialIncomplete = (o: PostOptions, commercialOn: boolean) => commercialOn && !o.tikTokBrandOrganic && !o.tikTokBrandedContent;
