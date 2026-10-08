import { ArrowRight } from "lucide-react";
import { useState } from "react";
import { Link } from "react-router";
import { errorMessage } from "../api/client";
import { canAdmin, keys, useApiMutation } from "../api/hooks";
import { Alert, Button, Field, Input, PageHeader, Panel } from "../components/ui";
import { useOrg } from "../components/org";

export function OrgSettingsPage() {
  const org = useOrg();
  const [name, setName] = useState(org.name);
  const rename = useApiMutation(() => ({ path: `/orgs/${org.id}`, method: "PATCH", body: { name } }), [keys.me]);

  return (
    <>
      <PageHeader title="Impostazioni" description="Le impostazioni generali dell'organizzazione." />

      <Panel
        title="Generale"
        className="mb-6"
        footer={
          canAdmin(org.role) && (
            <Button variant="primary" loading={rename.isPending} disabled={!name.trim() || name === org.name} onClick={() => rename.mutate()}>
              Salva
            </Button>
          )
        }
      >
        <div className="max-w-md space-y-3 p-4">
          <Field label="Nome dell'organizzazione">
            <Input value={name} onChange={(e) => setName(e.target.value)} disabled={!canAdmin(org.role)} />
          </Field>
          {rename.error && <Alert tone="bad">{errorMessage(rename.error)}</Alert>}
        </div>
      </Panel>

      <Link to={`/o/${org.id}/billing`} className="flex items-center justify-between rounded-lg border border-line bg-panel px-4 py-3 hover:bg-hover/50">
        <span>
          <span className="block text-[13px] text-fg">Piano e fatturazione</span>
          <span className="block text-xs text-muted">Abbonamento, utilizzo e dati per le fatture.</span>
        </span>
        <ArrowRight className="size-4 text-faint" />
      </Link>
    </>
  );
}
