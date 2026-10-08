import clsx from "clsx";
import { Check, CreditCard, FileText } from "lucide-react";
import { useState, type ReactNode } from "react";
import { ApiError, errorMessage } from "../api/client";
import { keys, useApiMutation, useBilling, usePlans } from "../api/hooks";
import type { BillingProfile, Plan } from "../api/types";
import { Alert, Badge, Button, EmptyState, Field, Input, PageHeader, PageLoader, Panel, Select } from "../components/ui";
import { formatPrice, useOrg } from "../components/org";

/**
 * Abbonamento, intestatario e fatture. Finché i pagamenti sono in sordina la
 * pagina lo dice chiaramente: niente carte da inserire, niente fatture.
 */
export function BillingPage() {
  const org = useOrg();
  const billing = useBilling(org.id);
  const plans = usePlans();

  if (billing.isPending || plans.isPending) return <PageLoader />;
  if (billing.error) return <Alert tone="bad">{errorMessage(billing.error)}</Alert>;

  const { subscription, memberCount, profile } = billing.data;
  const owner = org.role === "Owner";
  const invalidate = [keys.billing(org.id), keys.subscription(org.id)];

  return (
    <>
      <PageHeader title="Fatturazione" description="Piano, utilizzo, dati per le fatture." />

      <div className="mb-6">
        <Alert tone="info" title="Pagamenti non ancora attivi">
          Puoi scegliere e cambiare piano liberamente: vale subito e non viene addebitato niente.
        </Alert>
      </div>

      <div className="mb-6 grid gap-3 md:grid-cols-3">
        <SummaryCard label="Piano attuale">
          <div className="flex items-center gap-2">
            <span className="text-2xl font-medium tracking-tight text-fg">{subscription.plan.name}</span>
            {subscription.isActive ? <Badge tone="ok">Attivo</Badge> : <Badge tone="bad">Annullato</Badge>}
          </div>
          <p className="mt-1 text-xs text-muted">{formatPrice(subscription.plan.monthlyPriceCents)} al mese</p>
        </SummaryCard>

        <SummaryCard label="Utilizzo">
          <Usage label="Progetti" used={subscription.projectCount} limit={subscription.plan.maxProjects} />
          <p className="mt-3 text-xs text-muted">{memberCount} {memberCount === 1 ? "membro" : "membri"} · illimitati in tutti i piani</p>
        </SummaryCard>

        <SummaryCard label="Prossimo addebito">
          <span className="text-2xl font-medium tracking-tight text-fg">—</span>
          <p className="mt-1 text-xs text-muted">Nessun addebito finché i pagamenti non sono attivi.</p>
        </SummaryCard>
      </div>

      <PlansPanel orgId={org.id} plans={plans.data!} current={subscription.plan.code} active={subscription.isActive} owner={owner} invalidate={invalidate} />

      <div className="mt-6 grid gap-6 lg:grid-cols-2">
        <Panel title="Metodo di pagamento">
          <EmptyState icon={<CreditCard className="size-5" />} title="Nessun metodo di pagamento">
            Quando i pagamenti saranno attivi potrai aggiungere qui una carta o un addebito SEPA.
          </EmptyState>
        </Panel>
        <Panel title="Fatture">
          <EmptyState icon={<FileText className="size-5" />} title="Nessuna fattura">
            Le fatture compariranno qui, scaricabili in PDF, con il primo pagamento.
          </EmptyState>
        </Panel>
      </div>

      <ProfilePanel orgId={org.id} profile={profile} owner={owner} />

      {owner && subscription.isActive && <CancelPanel orgId={org.id} invalidate={invalidate} />}
    </>
  );
}

function SummaryCard({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="rounded-lg border border-line bg-panel p-4">
      <p className="mb-2 text-xs text-muted">{label}</p>
      {children}
    </div>
  );
}

function Usage({ label, used, limit }: { label: string; used: number; limit: number | null }) {
  const ratio = limit ? Math.min(used / limit, 1) : 0;
  return (
    <div>
      <div className="flex items-baseline justify-between text-[13px]">
        <span className="text-fg">{label}</span>
        <span className="text-muted tabular-nums">{limit == null ? `${used} · illimitati` : `${used} di ${limit}`}</span>
      </div>
      {limit != null && (
        <div className="mt-2 h-1.5 overflow-hidden rounded-full bg-panel-2">
          <div className={clsx("h-full rounded-full", ratio >= 1 ? "bg-warn" : "bg-brand")} style={{ width: `${ratio * 100}%` }} />
        </div>
      )}
    </div>
  );
}

function PlansPanel({ orgId, plans, current, active, owner, invalidate }: {
  orgId: string;
  plans: Plan[];
  current: string;
  active: boolean;
  owner: boolean;
  invalidate: readonly (readonly unknown[])[];
}) {
  const change = useApiMutation<string>((plan) => ({ path: `/orgs/${orgId}/subscription`, method: "PUT", body: { plan } }), invalidate);

  return (
    <Panel title="Piani" description={active ? undefined : "L'abbonamento è annullato: scegli un piano per riattivarlo."}>
      <div className="space-y-4 p-4">
        <div className="grid gap-3 md:grid-cols-3">
          {plans.map((plan) => {
            const isCurrent = plan.code === current && active;
            return (
              <div key={plan.code} className={clsx("flex flex-col rounded-lg border p-4", isCurrent ? "border-brand/60 bg-brand/5" : "border-line bg-panel-2/40")}>
                <div className="flex items-center justify-between">
                  <span className="text-[13px] font-medium text-fg">{plan.name}</span>
                  {isCurrent && <Badge tone="brand">Attuale</Badge>}
                </div>
                <p className="mt-3 text-2xl font-medium tracking-tight text-fg">
                  {formatPrice(plan.monthlyPriceCents)}
                  <span className="text-xs font-normal text-muted"> /mese</span>
                </p>
                <ul className="mt-3 flex-1 space-y-1.5 text-xs text-muted">
                  <li className="flex items-center gap-1.5"><Check className="size-3 text-ok" />{plan.maxProjects == null ? "Progetti illimitati" : `Fino a ${plan.maxProjects} progetti`}</li>
                  <li className="flex items-center gap-1.5"><Check className="size-3 text-ok" />App Store e Google Play</li>
                  <li className="flex items-center gap-1.5"><Check className="size-3 text-ok" />Membri illimitati</li>
                </ul>
                {owner && !isCurrent && (
                  <Button className="mt-4" variant={active ? "secondary" : "primary"} loading={change.isPending && change.variables === plan.code} onClick={() => change.mutate(plan.code)}>
                    {active ? `Passa a ${plan.name}` : `Riattiva con ${plan.name}`}
                  </Button>
                )}
              </div>
            );
          })}
        </div>
        {change.error && <Alert tone="bad">{errorMessage(change.error)}</Alert>}
        {!owner && <p className="text-xs text-faint">Solo un owner può cambiare piano.</p>}
      </div>
    </Panel>
  );
}

const countries: [string, string][] = [
  ["IT", "Italia"], ["SM", "San Marino"], ["CH", "Svizzera"], ["DE", "Germania"], ["FR", "Francia"], ["ES", "Spagna"],
  ["AT", "Austria"], ["NL", "Paesi Bassi"], ["BE", "Belgio"], ["PT", "Portogallo"], ["IE", "Irlanda"], ["GB", "Regno Unito"], ["US", "Stati Uniti"],
];

const emptyProfile: BillingProfile = {
  companyName: "", vatNumber: null, taxCode: null, addressLine: "", city: "", postalCode: "",
  province: null, countryCode: "IT", billingEmail: "", sdiCode: null, pec: null,
};

/** L'intestatario delle fatture. I campi della fattura elettronica compaiono solo per l'Italia. */
function ProfilePanel({ orgId, profile, owner }: { orgId: string; profile: BillingProfile | null; owner: boolean }) {
  const [form, setForm] = useState<BillingProfile>(profile ?? emptyProfile);
  const [saved, setSaved] = useState(false);
  const save = useApiMutation<void, BillingProfile>(() => ({ path: `/orgs/${orgId}/billing/profile`, method: "PUT", body: form }), [keys.billing(orgId)]);

  const italy = form.countryCode === "IT";
  const fieldErrors = save.error instanceof ApiError ? save.error.fieldErrors : {};
  const err = (name: string) => fieldErrors[name]?.[0];

  const text = (key: keyof BillingProfile) => ({
    value: form[key] ?? "",
    disabled: !owner,
    onChange: (e: React.ChangeEvent<HTMLInputElement>) => {
      setSaved(false);
      setForm({ ...form, [key]: e.target.value || null });
    },
  });

  return (
    <Panel
      title="Dati di fatturazione"
      description="L'intestatario delle fatture."
      className="mt-6"
      footer={owner && (
        <Button variant="primary" loading={save.isPending} onClick={() => save.mutate(undefined, { onSuccess: () => setSaved(true) })}>
          Salva
        </Button>
      )}
    >
      <div className="grid gap-4 p-4 sm:grid-cols-6">
        <div className="sm:col-span-4">
          <Field label="Ragione sociale o nome" error={err("CompanyName")}><Input {...text("companyName")} /></Field>
        </div>
        <div className="sm:col-span-2">
          <Field label="Paese" error={err("CountryCode")}>
            <Select value={form.countryCode} disabled={!owner} onChange={(e) => setForm({ ...form, countryCode: e.target.value })}>
              {countries.map(([code, name]) => <option key={code} value={code}>{name}</option>)}
            </Select>
          </Field>
        </div>

        <div className="sm:col-span-3">
          <Field label={italy ? "Partita IVA" : "Numero di partita IVA (VAT)"} error={err("VatNumber")}>
            <Input className="font-mono" placeholder={italy ? "01234567890" : "DE123456789"} {...text("vatNumber")} />
          </Field>
        </div>
        {italy && (
          <div className="sm:col-span-3">
            <Field label="Codice fiscale" hint="Se diverso dalla partita IVA, o se sei un privato." error={err("TaxCode")}>
              <Input className="font-mono" {...text("taxCode")} />
            </Field>
          </div>
        )}

        <div className="sm:col-span-6">
          <Field label="Indirizzo" error={err("AddressLine")}><Input {...text("addressLine")} /></Field>
        </div>
        <div className="sm:col-span-3">
          <Field label="Città" error={err("City")}><Input {...text("city")} /></Field>
        </div>
        <div className={italy ? "sm:col-span-2" : "sm:col-span-3"}>
          <Field label="CAP" error={err("PostalCode")}><Input {...text("postalCode")} /></Field>
        </div>
        {italy && (
          <div className="sm:col-span-1">
            <Field label="Prov." error={err("Province")}><Input maxLength={2} className="uppercase" {...text("province")} /></Field>
          </div>
        )}

        <div className={italy ? "sm:col-span-6" : "sm:col-span-6"}>
          <Field label="Email per le fatture" error={err("BillingEmail")}><Input type="email" {...text("billingEmail")} /></Field>
        </div>

        {italy && (
          <>
            <div className="sm:col-span-6 -mb-1 border-t border-line pt-4">
              <p className="text-[13px] text-fg">Fattura elettronica</p>
              <p className="text-xs text-muted">Per le aziende serve il codice destinatario SDI oppure la PEC.</p>
            </div>
            <div className="sm:col-span-2">
              <Field label="Codice destinatario SDI" error={err("SdiCode")}>
                <Input maxLength={7} className="font-mono uppercase" placeholder="0000000" {...text("sdiCode")} />
              </Field>
            </div>
            <div className="sm:col-span-4">
              <Field label="PEC" error={err("Pec")}><Input type="email" placeholder="amministrazione@pec.it" {...text("pec")} /></Field>
            </div>
          </>
        )}

        <div className="sm:col-span-6">
          {save.error && Object.keys(fieldErrors).length === 0 && <Alert tone="bad">{errorMessage(save.error)}</Alert>}
          {save.error && Object.keys(fieldErrors).length > 0 && <Alert tone="bad">Controlla i campi evidenziati.</Alert>}
          {saved && <Alert tone="ok">Dati salvati.</Alert>}
          {!owner && <p className="text-xs text-faint">Solo un owner può modificare i dati di fatturazione.</p>}
        </div>
      </div>
    </Panel>
  );
}

function CancelPanel({ orgId, invalidate }: { orgId: string; invalidate: readonly (readonly unknown[])[] }) {
  const [confirm, setConfirm] = useState(false);
  const cancel = useApiMutation(() => ({ path: `/orgs/${orgId}/subscription/cancel` }), invalidate);

  return (
    <div className="mt-6 rounded-lg border border-bad/30 bg-panel p-4">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div>
          <p className="text-[13px] font-medium text-fg">Annulla l'abbonamento</p>
          <p className="mt-0.5 text-xs text-muted">Progetti, chiavi e dati restano e si continuano a vedere; le modifiche si bloccano finché non scegli di nuovo un piano.</p>
        </div>
        {confirm ? (
          <div className="flex gap-2">
            <Button variant="danger" loading={cancel.isPending} onClick={() => cancel.mutate(undefined, { onSuccess: () => setConfirm(false) })}>Sì, annulla</Button>
            <Button variant="ghost" onClick={() => setConfirm(false)}>No</Button>
          </div>
        ) : (
          <Button variant="danger" onClick={() => setConfirm(true)}>Annulla abbonamento</Button>
        )}
      </div>
      {cancel.error && <div className="mt-3"><Alert tone="bad">{errorMessage(cancel.error)}</Alert></div>}
    </div>
  );
}
