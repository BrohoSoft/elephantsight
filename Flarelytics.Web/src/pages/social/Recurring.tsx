import { Copy, Pause, Play, Plus, Repeat } from "lucide-react";
import { useState } from "react";
import { canAdmin, keys, useApiMutation, useProjects, useSocialAccounts, useSocialRecurring } from "../../api/hooks";
import type { RecurringPost, SocialAccount } from "../../api/types";
import { formatDateTime, useOrg } from "../../components/org";
import { NetworkGlyph } from "../../components/SocialIcons";
import { Alert, Badge, Button, EmptyState, PageHeader, PageLoader, Panel } from "../../components/ui";
import { postStatus } from "../../social/PostEditor";
import { describeRule, duplicateTemplate } from "../../social/recurrence";
import { RecurringEditor } from "../../social/RecurringEditor";
import { accountLabel } from "../../social/rules";
import { errorMessage } from "../../api/client";
import { MediaThumb } from "./Inbox";

/**
 * I post ricorrenti: contenuti che escono da soli a intervalli regolari (ogni
 * giorno, certi giorni della settimana, una volta al mese). Ogni uscita
 * diventa un post del calendario, con il suo esito.
 */
export function SocialRecurringPage() {
  const org = useOrg();
  const admin = canAdmin(org.role);
  const recurring = useSocialRecurring(org.id);
  const accounts = useSocialAccounts(org.id);
  const projects = useProjects(org.id);
  // Si apre per modificare (recurring), per creare da una copia (template) o da zero.
  const [editing, setEditing] = useState<{ recurring?: RecurringPost; template?: RecurringPost } | null>(null);
  const duplicate = (copy: RecurringPost) => setEditing({ template: copy });

  if (recurring.isPending || accounts.isPending || projects.isPending) return <PageLoader />;

  return (
    <>
      <PageHeader
        title="Post ricorrenti"
        description="Contenuti che ElephantSight pubblica da solo a intervalli regolari. Ogni uscita compare nel calendario come un post, con il suo esito; le prossime sono segnate con il simbolo della ripetizione."
        actions={admin && <Button variant="primary" icon={<Plus className="size-3.5" />} onClick={() => setEditing({})}>Nuovo post ricorrente</Button>}
      />

      {recurring.error && <Alert tone="bad">{errorMessage(recurring.error)}</Alert>}

      {recurring.data!.length === 0 ? (
        <div className="rounded-lg border border-dashed border-line-strong">
          <EmptyState icon={<Repeat className="size-5" />} title="Nessun post ricorrente"
            action={admin && <Button variant="primary" onClick={() => setEditing({})}>Crea il primo</Button>}>
            Un consiglio ogni lunedì, il link allo store una volta al mese, un promemoria ogni giorno alle 9: lo scrivi una volta, esce da solo.
          </EmptyState>
        </div>
      ) : (
        <Panel>
          <ul className="divide-y divide-line">
            {recurring.data!.map((r) => (
              <RecurringRow key={r.id} recurring={r} accounts={accounts.data!} admin={admin} onOpen={() => setEditing({ recurring: r })} onDuplicate={duplicate} />
            ))}
          </ul>
        </Panel>
      )}

      {editing && (
        // La chiave rimonta l'editor quando da un post si passa alla sua copia.
        <RecurringEditor key={editing.recurring?.id ?? (editing.template ? "copia" : "nuovo")} recurring={editing.recurring} template={editing.template}
          accounts={accounts.data!} projects={projects.data ?? []} admin={admin} onClose={() => setEditing(null)} onDuplicate={duplicate} />
      )}
    </>
  );
}

function RecurringRow({ recurring: r, accounts, admin, onOpen, onDuplicate }: {
  recurring: RecurringPost;
  accounts: SocialAccount[];
  admin: boolean;
  onOpen: () => void;
  onDuplicate: (copy: RecurringPost) => void;
}) {
  const org = useOrg();
  const [copying, setCopying] = useState(false);
  const [copyError, setCopyError] = useState<unknown>(null);

  async function duplicate() {
    setCopying(true);
    setCopyError(null);
    try {
      onDuplicate(await duplicateTemplate(org.id, r));
    } catch (e) {
      setCopyError(e);
    } finally {
      setCopying(false);
    }
  }

  const pause = useApiMutation(
    () => ({ path: `/orgs/${org.id}/social/recurring/${r.id}/paused`, body: { paused: !r.isPaused } }),
    [keys.socialRecurring(org.id)],
  );
  const chosen = accounts.filter((a) => r.accountIds.includes(a.id));
  const last = r.lastPost;

  return (
    <li className="flex cursor-pointer flex-wrap items-start gap-x-4 gap-y-2 px-4 py-3 hover:bg-hover/40" onClick={onOpen}>
      <MediaThumb media={r.media} />
      <div className="min-w-0 flex-1 space-y-1">
        <p className="line-clamp-2 text-[0.8125rem] text-fg">{r.text || <span className="text-muted">Senza testo</span>}</p>
        <p className="flex items-start gap-1.5 text-xs text-muted">
          <Repeat className="mt-0.5 size-3 shrink-0" /> {describeRule(r)}
        </p>
        <p className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted">
          {chosen.length === 0
            ? <span className="text-bad">Nessun account: le uscite si saltano finché non ne scegli uno.</span>
            : chosen.map((a) => <span key={a.id} className="inline-flex items-center gap-1"><NetworkGlyph network={a.network} className="size-3" />{accountLabel(a)}</span>)}
        </p>
      </div>
      <div className="flex shrink-0 flex-col items-end gap-1.5 text-xs">
        {r.isPaused ? <Badge>In pausa</Badge> : r.nextOccurrenceUtc ? <span className="text-fg">Prossima: {formatDateTime(r.nextOccurrenceUtc)}</span> : <Badge>Finito</Badge>}
        {last && (
          <span className="flex items-center gap-1.5 text-muted">
            Ultima {formatDateTime(last.scheduledAtUtc)} <Badge tone={postStatus[last.status].tone}>{postStatus[last.status].label}</Badge>
          </span>
        )}
        {r.occurrenceCount > 0 && <span className="text-faint">{r.occurrenceCount === 1 ? "1 uscita" : `${r.occurrenceCount} uscite`}</span>}
        {admin && (
          <span className="flex gap-1">
            <Button size="sm" variant="ghost" loading={copying} icon={<Copy className="size-3" />} onClick={(e) => { e.stopPropagation(); duplicate(); }}>Duplica</Button>
            {(r.isPaused || r.nextOccurrenceUtc) && (
              <Button size="sm" variant="ghost" loading={pause.isPending} icon={r.isPaused ? <Play className="size-3" /> : <Pause className="size-3" />}
                onClick={(e) => { e.stopPropagation(); pause.mutate(); }}>
                {r.isPaused ? "Riprendi" : "Metti in pausa"}
              </Button>
            )}
          </span>
        )}
        {copyError ? <span className="text-bad">{errorMessage(copyError)}</span> : null}
      </div>
    </li>
  );
}
