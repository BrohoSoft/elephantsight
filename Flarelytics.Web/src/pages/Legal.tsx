import type { ReactNode } from "react";
import { Link } from "react-router";
import { useInstance } from "../api/hooks";
import { Logo } from "../components/Logo";
import { ThemeSwitcher } from "../components/ThemeSwitcher";
import { PageLoader } from "../components/ui";

/*
 * Informativa privacy e termini d'uso di questa installazione, pubbliche e
 * senza login: TikTok e Meta le chiedono per approvare un'app. Descrivono
 * quello che WatchStore fa davvero (uno strumento privato con cui chi lo
 * gestisce pubblica sui propri account), in inglese per chi fa la revisione e
 * in italiano sotto. Titolare e contatto vengono dalla configurazione
 * (LEGAL_OWNER, LEGAL_CONTACT_EMAIL).
 */

const UPDATED_EN = "9 October 2026";
const UPDATED_IT = "9 ottobre 2026";

function useLegal() {
  const instance = useInstance();
  const owner = instance.data?.legalOwner ?? "the operator of this installation";
  const ownerIt = instance.data?.legalOwner ?? "chi gestisce questa installazione";
  const email = instance.data?.legalContactEmail;
  const site = window.location.origin;
  return { loading: instance.isPending, owner, ownerIt, email, site, configured: !!(instance.data?.legalOwner && instance.data?.legalContactEmail) };
}

function LegalLayout({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="min-h-full px-4 py-10">
      <div className="mx-auto max-w-3xl">
        <div className="mb-8 flex items-center justify-between">
          <Link to="/"><Logo /></Link>
          <ThemeSwitcher compact />
        </div>
        <h1 className="text-2xl font-medium tracking-tight text-fg">{title}</h1>
        <div className="mt-6 space-y-10 text-[0.875rem] leading-relaxed text-muted [&_h2]:mt-8 [&_h2]:mb-2 [&_h2]:text-base [&_h2]:font-medium [&_h2]:text-fg [&_li]:mt-1 [&_p]:mt-3 [&_strong]:text-fg [&_ul]:mt-2 [&_ul]:list-disc [&_ul]:pl-5">
          {children}
        </div>
        <p className="mt-12 border-t border-line pt-4 text-xs text-faint">
          <Link to="/privacy" className="hover:text-fg">Privacy Policy</Link> · <Link to="/terms" className="hover:text-fg">Terms of Service</Link>
        </p>
      </div>
    </div>
  );
}

function Contact({ email, fallback }: { email?: string | null; fallback: string }) {
  return email ? <a className="text-brand-fg hover:underline" href={`mailto:${email}`}>{email}</a> : <span>{fallback}</span>;
}

function NotConfigured({ configured }: { configured: boolean }) {
  if (configured) return null;
  return (
    <p className="rounded-md border border-warn/40 bg-warn/5 px-3 py-2 text-[0.8125rem] text-warn">
      Per chi gestisce l'istanza: imposta LEGAL_OWNER e LEGAL_CONTACT_EMAIL nel file .env, così qui compaiono il tuo nome e l'email di contatto.
    </p>
  );
}

export function PrivacyPage() {
  const { loading, owner, ownerIt, email, site, configured } = useLegal();
  if (loading) return <PageLoader />;

  return (
    <LegalLayout title="Privacy Policy">
      <NotConfigured configured={configured} />
      <section>
        <p className="text-xs text-faint">Last updated: {UPDATED_EN}</p>

        <h2>1. Who we are</h2>
        <p>
          WatchStore at <strong>{site}</strong> is a private, self-hosted tool operated by <strong>{owner}</strong> (the "Operator").
          It is used internally by the Operator and by people the Operator invites, to manage the Operator's own mobile apps
          and to prepare, schedule and publish content on the Operator's own social media accounts (TikTok, Instagram, Facebook Pages, Bluesky, Mastodon).
          It is not a public service: there is no open sign-up, and every user is invited by the Operator.
        </p>

        <h2>2. Data we process</h2>
        <ul>
          <li><strong>Account data of invited users</strong>: name, email address, password (stored only as a hash), optional two-factor secret (encrypted).</li>
          <li>
            <strong>Data received from TikTok</strong>, only after the account owner logs in with TikTok and authorizes the app:
            the account's open ID, display name and username, the creator information TikTok returns before posting (allowed privacy levels,
            whether comments, duets and stitches are enabled, maximum video length), and the access and refresh tokens.
            Scopes requested: <code>user.info.basic</code> and <code>video.publish</code>.
          </li>
          <li><strong>Data received from Meta (Instagram, Facebook)</strong>, Bluesky and Mastodon after the account owner connects them: account ID, name or username, access tokens, and the account's own recently published posts (caption, date, link and a preview image), shown in the calendar.</li>
          <li><strong>Content uploaded by users</strong>: videos, images, captions, schedule and per-post settings, and the publishing result returned by each platform.</li>
        </ul>
        <p>We do not collect data about other TikTok or Instagram users, followers, comments or messages, and we do not use any data for advertising, profiling or analytics.</p>

        <h2>3. Why we process it</h2>
        <p>
          Only to publish the content that a user explicitly creates and schedules, on the accounts that the account owner connected,
          with the settings chosen by the user for each post (for TikTok: who can view the video, comments, duets, stitches and commercial content disclosure);
          and to show the publishing status and the link to the published post. Nothing is published without an explicit action by a user.
        </p>

        <h2>4. Where data is stored and how it is protected</h2>
        <p>
          All data is stored on a server controlled by the Operator. Access and refresh tokens are encrypted at rest (AES-256-GCM) and are never shown
          or returned to users; data of different organizations is isolated at database level. Connections use HTTPS.
        </p>

        <h2>5. Sharing</h2>
        <p>
          We do not sell or share personal data with third parties. Content is sent only to the platform the user chose for that post
          (for example the video and caption to TikTok when it is published). There are no advertising or analytics services.
        </p>

        <h2>6. Retention and deletion</h2>
        <ul>
          <li>Disconnecting a social account in WatchStore deletes its tokens immediately.</li>
          <li>Deleting a post deletes its uploaded media from the server.</li>
          <li>Media that is uploaded but never used in a post is deleted after one day.</li>
          <li>You can also revoke WatchStore's access at any time from the platform: on TikTok in Settings and privacy → Security and permissions → Apps and services.</li>
          <li>To have all your data deleted, write to <Contact email={email} fallback="the Operator" />; we act within 30 days.</li>
        </ul>

        <h2>7. Your rights</h2>
        <p>
          You can ask for access to, correction or deletion of your data, and object to its processing, by writing to <Contact email={email} fallback="the Operator" />.
          If you are in the European Union you can also lodge a complaint with your data protection authority.
        </p>

        <h2>8. Contact</h2>
        <p>{owner} · <Contact email={email} fallback="contact email not configured" /></p>
      </section>

      <section className="border-t border-line pt-8">
        <h2 className="!mt-0 text-lg">Informativa privacy (italiano)</h2>
        <p className="text-xs text-faint">Ultimo aggiornamento: {UPDATED_IT}</p>
        <p>
          WatchStore su <strong>{site}</strong> è uno strumento privato e installato sui propri server da <strong>{ownerIt}</strong> (il "Titolare"),
          usato dal Titolare e dalle persone che invita per gestire le proprie app e preparare, programmare e pubblicare contenuti sui propri account social
          (TikTok, Instagram, Pagine Facebook, Bluesky, Mastodon). Non è un servizio aperto al pubblico: non c'è registrazione libera.
        </p>
        <ul>
          <li><strong>Dati trattati</strong>: dati degli utenti invitati (nome, email, password solo come hash); da TikTok, dopo il login del titolare dell'account, open ID, nome e username, le informazioni del creator e i token d'accesso (scope <code>user.info.basic</code> e <code>video.publish</code>); da Meta, Bluesky e Mastodon ID, nome e token, e i post già pubblicati dall'account; i video, le immagini e i testi caricati.</li>
          <li><strong>Finalità</strong>: solo pubblicare i contenuti che un utente crea e programma, sugli account collegati, con le impostazioni che sceglie per ogni post; mostrare l'esito e il link. Niente pubblicità, profilazione o statistiche, nessun dato di altri utenti dei social.</li>
          <li><strong>Conservazione e sicurezza</strong>: su un server del Titolare; token cifrati (AES-256-GCM) e mai mostrati; dati delle organizzazioni separati nel database; connessioni HTTPS.</li>
          <li><strong>Comunicazione a terzi</strong>: nessuna vendita né cessione; i contenuti vanno solo alla piattaforma scelta per quel post.</li>
          <li><strong>Cancellazione</strong>: scollegando un account se ne cancellano subito i token; cancellando un post se ne cancellano i file; i file mai usati si cancellano dopo un giorno. L'accesso si revoca anche dalle impostazioni della piattaforma. Per cancellare tutti i dati scrivi a <Contact email={email} fallback="il Titolare" />: si provvede entro 30 giorni.</li>
          <li><strong>Diritti</strong>: accesso, rettifica, cancellazione, opposizione scrivendo a <Contact email={email} fallback="il Titolare" />; reclamo al Garante per la protezione dei dati personali.</li>
        </ul>
      </section>
    </LegalLayout>
  );
}

export function TermsPage() {
  const { loading, owner, ownerIt, email, site, configured } = useLegal();
  if (loading) return <PageLoader />;

  return (
    <LegalLayout title="Terms of Service">
      <NotConfigured configured={configured} />
      <section>
        <p className="text-xs text-faint">Last updated: {UPDATED_EN}</p>

        <h2>1. The service</h2>
        <p>
          WatchStore at <strong>{site}</strong> is a private, self-hosted tool operated by <strong>{owner}</strong>. It lets the Operator and
          invited users manage the Operator's mobile apps and prepare, schedule and publish content on social media accounts that the account owners
          connect themselves. It is not offered to the public and access is by invitation only.
        </p>

        <h2>2. Accounts</h2>
        <p>
          Users are invited by the Operator and are responsible for keeping their credentials safe; two-factor authentication is available and recommended.
          The Operator can suspend or remove access at any time.
        </p>

        <h2>3. Connected social accounts</h2>
        <p>
          A social account can be connected only by its owner or by someone authorized to act for it, through the platform's own login.
          Access can be revoked at any time, in WatchStore or in the platform's settings.
        </p>

        <h2>4. Content and publishing</h2>
        <ul>
          <li>Users publish only content they own or have the rights to use, and are responsible for it.</li>
          <li>Content is published only when a user schedules it or chooses "publish now", with the visibility and interaction settings the user selects for each post.</li>
          <li>
            Users must follow the rules of each platform. For TikTok this includes the{" "}
            <a className="text-brand-fg hover:underline" href="https://www.tiktok.com/legal/terms-of-service" target="_blank" rel="noreferrer">Terms of Service</a>,
            the <a className="text-brand-fg hover:underline" href="https://www.tiktok.com/legal/page/global/music-usage-confirmation/en" target="_blank" rel="noreferrer">Music Usage Confirmation</a>
            {" "}and, for branded content, the <a className="text-brand-fg hover:underline" href="https://www.tiktok.com/legal/page/global/bc-policy/en" target="_blank" rel="noreferrer">Branded Content Policy</a>;
            commercial content must be disclosed with the options shown before publishing.
          </li>
          <li>Platforms may process, delay or reject content according to their own rules; WatchStore shows the result each platform returns.</li>
        </ul>

        <h2>5. Personal data</h2>
        <p>How data is handled is described in the <Link to="/privacy" className="text-brand-fg hover:underline">Privacy Policy</Link>.</p>

        <h2>6. Availability and liability</h2>
        <p>
          The tool is provided "as is" for the Operator's internal use, without guarantees of availability. To the extent permitted by law, the Operator is not
          liable for indirect damages or for the actions of the connected platforms.
        </p>

        <h2>7. Changes and contact</h2>
        <p>These terms may change; the date above shows the latest version. Contact: {owner} · <Contact email={email} fallback="contact email not configured" /></p>
      </section>

      <section className="border-t border-line pt-8">
        <h2 className="!mt-0 text-lg">Termini d'uso (italiano)</h2>
        <p className="text-xs text-faint">Ultimo aggiornamento: {UPDATED_IT}</p>
        <ul>
          <li><strong>Il servizio</strong>: WatchStore su {site} è uno strumento privato di {ownerIt}, per gestire le proprie app e pubblicare sui propri account social. Si accede solo su invito.</li>
          <li><strong>Account</strong>: gli utenti custodiscono le proprie credenziali (la verifica in due passaggi è consigliata); il Titolare può sospendere l'accesso in qualsiasi momento.</li>
          <li><strong>Account social</strong>: li collega solo chi ne è titolare o autorizzato, con il login della piattaforma; l'accesso si revoca in qualsiasi momento.</li>
          <li><strong>Contenuti</strong>: si pubblicano solo contenuti propri o di cui si hanno i diritti, solo quando un utente li programma, con le impostazioni scelte per ogni post. Valgono le regole di ogni piattaforma; per TikTok i Termini, la Music Usage Confirmation e, per i contenuti commerciali, la Branded Content Policy.</li>
          <li><strong>Responsabilità</strong>: lo strumento è fornito così com'è per uso interno, senza garanzie di disponibilità, nei limiti di legge.</li>
          <li><strong>Contatti</strong>: {ownerIt} · <Contact email={email} fallback="email non configurata" /></li>
        </ul>
      </section>
    </LegalLayout>
  );
}
