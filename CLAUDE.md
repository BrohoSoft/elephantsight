# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Cos'è

Flarelytics è un SaaS multi-tenant che mette in un'unica dashboard i dati di App Store Connect e Google Play. Un'organizzazione (tenant) ha più progetti; ogni progetto collega al massimo un'app App Store e un'app Google Play, attraverso le chiavi degli store che il tenant carica (il .p8 di Apple, il JSON di un service account Google).

Stato: fase 1 completa e fase 2 avviata (sincronizzazione delle vendite Apple), cioè account con 2FA, organizzazioni con membri e inviti, progetti, credenziali cifrate, pagamenti finti e pannello React. Fasi successive: worker di sincronizzazione e metriche giornaliere (schema `metrics` su Postgres, report grezzi conservati per poterli rielaborare; i box degli indicatori nella UI per ora sono segnaposto), poi pagamenti veri.

Codice, commenti e messaggi d'errore sono in italiano; i commenti spiegano il *perché*. Mantieni lo stesso stile.

## Comandi

```bash
docker compose -f compose.dev.yaml up -d                          # PostgreSQL di sviluppo su :5433
cd Flarelytics.Api && dotnet run                                   # API su http://localhost:5080, Scalar su /scalar
cd Flarelytics.Web && npm install && npm run dev                   # pannello su http://localhost:5173 (proxy /api → 5080)
cd Flarelytics.Web && npm run build                                # typecheck + build di produzione
cd Flarelytics.Worker && dotnet run                                # worker di sincronizzazione (con l'API già avviata: è lei ad applicare le migration)
dotnet build
dotnet test                                                        # serve Docker (Testcontainers)
dotnet test --filter "FullyQualifiedName~CredentialTests"          # una classe
dotnet test --filter "FullyQualifiedName~SecretVaultTests.Un_byte_alterato_si_riconosce"   # un test
dotnet ef migrations add <Nome> --project Flarelytics.Core --startup-project Flarelytics.Core --output-dir Database/Migrations
```

In sviluppo le email non partono: link di conferma e reset si leggono nel log dell'API. La chiave master di sviluppo viene creata da sola in `.dev/keys` (solo con ambiente Development). `dotnet run --environment` non imposta l'ambiente: usa il launch profile oppure `ASPNETCORE_ENVIRONMENT=Development`.

## Architettura

- **Flarelytics.Core**: modello EF, isolamento dei tenant, cifratura, client degli store, piani/billing. Niente HTTP in ingresso: lo userà anche il worker della fase 2.
- **Flarelytics.Worker**: processo separato, una sola istanza, che ogni `Sync:PollInterval` chiama `SyncCoordinator.RunOnceAsync`.
- **Flarelytics.Api**: minimal API sotto `/api/v1`, un file per feature in `Features/` (handler statici, richieste, validatori FluentValidation nello stesso file).
- **Flarelytics.Tests**: integrazione con Testcontainers (un database per test) e unit test. `MigrationTests` applica le migration una per volta sopra dati già presenti: una colonna NOT NULL nuova vuole un valore predefinito, altrimenti in produzione l'API non parte.
- **Flarelytics.Web**: React 19 + Vite + Tailwind 4 + TanStack Query + React Router, stile Supabase con tema chiaro, scuro o di sistema: i colori sono variabili CSS in `src/styles.css` (`@theme inline` → `var(--…)`, una palette per `[data-theme]`); `public/theme.js` applica il tema prima del primo disegno (file e non script inline, per la CSP) e `src/theme.ts` lo gestisce dopo. Per un colore nuovo serve un valore in entrambe le palette. Componenti base in `src/components/ui.tsx`, tipi delle risposte scritti a mano in `src/api/types.ts` (da allineare ai record C#). In produzione è servito da Caddy (`Flarelytics.Web/Dockerfile`).

### Isolamento dei tenant: due livelli, entrambi obbligatori
1. Le entità `ITenantOwned` ricevono un filtro globale EF su `TenantContext.TenantId` (`FlarelyticsDbContext`). Con il tenant non impostato le tabelle appaiono vuote.
2. Row-Level Security di PostgreSQL. `TenantConnectionInterceptor` scrive `app.tenant_id` a ogni apertura di connessione; le politiche sono create in migration con `RowLevelSecurity.Enable(...)`. **Una nuova tabella `ITenantOwned` ha bisogno della sua chiamata `RowLevelSecurity.Enable` nella migration**: non è automatico.

`TenantContext.Set` si chiama solo in `OrgAccessFilter` (tutte le rotte `/orgs/{orgId}/...`, che controllano la membership e il ruolo) e nei flussi che creano un tenant. Se la richiesta usa una transazione esplicita, il tenant va impostato prima di aprirla. L'app deve collegarsi con un ruolo **non superutente e senza BYPASSRLS**, proprietario delle tabelle: per questo le politiche sono `FORCE`. I test fanno lo stesso (`PostgresFixture`), altrimenti i test sulla RLS passerebbero senza verificare niente.

`Tenant`, `User`, `Membership`, `RefreshToken`, `EmailToken` non sono del tenant: si leggono prima di aver scelto un tenant.

### Credenziali degli store
- Metadati (Key ID, Issuer ID, email del service account, bucket) in `StoreCredential`; il segreto vero solo cifrato su disco tramite `SecretVault`. Nessuna rotta restituisce il segreto.
- Cifratura a busta: una DEK casuale per file (AES-256-GCM), cifrata con la chiave master (KEK) versionata letta da `Secrets:KeysDirectory`. Tenant e credenziale entrano come dati associati, quindi un file spostato non si decifra. Rotazione: nuova chiave + `ActiveKeyVersion` + `SecretVault.RewrapAsync`.
- Il segreto si usa solo tramite `CredentialSecrets.UseAsync`, che lo azzera dopo l'uso.
- Al caricamento: parse (`AppleKey`/`GoogleServiceAccount`) → verifica con lo store (`IStoreGateway`) → cifratura → salvataggio. `Rejected` non si salva; `Limited` sì, con l'avviso (i permessi di Google Play arrivano ore dopo l'invito).
- Gateway senza SDK: JWT ES256 firmato da noi per Apple; JWT RS256 scambiato con un token OAuth per Google (`GoogleAuth`).
- **Google: solo il bucket GCS `pubsite_prod_…`, mai la Play Developer Reporting API.** I suoi termini (marzo 2022) vietano di usarla con l'account sviluppatore di un terzo, che è il caso di Flarelytics. Verifica della chiave ed elenco delle app (dai nomi dei file) passano dal bucket.
- **Termini Apple:** i termini dell'API App Store Connect, citati in un post del forum sviluppatori del 2023, ne limitano l'uso al team interno e vietano di chiedere credenziali a terzi. Il punto è aperto e va chiarito con Apple prima di vendere il servizio.
- `ProjectApp.ExternalAppId` è l'Apple ID numerico (non il bundle id) oppure il package name Android.

### Sincronizzazione e metriche (fase 2)
- Flusso: `SyncCoordinator` → cambi BCE (`RatesRefresher`) → per ogni tenant con abbonamento attivo apre uno scope, imposta `TenantContext` (la RLS vale anche nel worker) e chiama `AppleSalesSync` per le credenziali App Store da sincronizzare (ogni `Sync:Interval`, o subito con "Sincronizza ora" = `SyncRequestedAtUtc`).
- `AppleSalesSync` scarica SALES/SUMMARY/DAILY versione `1_0` per ogni giorno mancante degli ultimi `BackfillDays` (dal più recente), e ritenta i giorni vuoti recenti: Apple risponde 404 sia per "nessuna vendita" sia per "non ancora pronto". I giorni seguono il fuso del Pacifico (`AppleCalendar`). 401 rende la chiave `Invalid`, 403 `Limited`.
- Google Play: `GooglePlaySync` legge `stats/installs/installs_<package>_<aaaamm>_country.csv` (UTF-16, colonne per nome con alternative; Download = "Daily User Installs"), riscarica un file mensile solo se cambia l'MD5, e riscrive solo le colonne delle installazioni (download, aggiornamenti, disinstallazioni).
- Google, finanza (`GoogleFinanceParser`): `sales/salesreport_<aaaamm>.zip` dà acquisti in-app, rimborsi e venduto lordo (ordine per ordine, giorno UTC); `earnings/earnings_<aaaamm>….zip` dà il netto (somma di incasso, commissione, tasse, rimborsi; giorno del Pacifico; più file per mese). Si rielaborano per **mese intero** e ogni report scrive solo le sue colonne, così installazioni, vendite e guadagni convivono sulle stesse righe. Il segno nei rimborsi del report vendite lo decide lo stato, non l'importo: la documentazione non lo chiarisce, va verificato sui file veri.
- `StoreCoverage` dice quali metriche ogni store fornisce (per Google dipende dai report arrivati) e fino a quando i ricavi netti Google sono completi (`ProceedsThrough`, fine dell'ultimo mese di guadagni): la UI mostra "—" e note, mai zeri finti.
- I report grezzi (gzip, così come arrivano) stanno su disco tramite `ReportStorage` e sono la fonte di verità: `ReportProcessor` ne ricava `DailyAppMetric` senza rete. Cambiando il modo di contare si alza `AppleSalesAggregator.Version` e i file vengono rielaborati al giro successivo.
- `DailyAppMetric` (schema `metrics`, chiave tenant+store+app+giorno+paese, importi in micro-euro) è legata all'app, non al progetto: i progetti la leggono tramite `ProjectApp.ExternalAppId`. Gli acquisti in-app si attribuiscono all'app tramite lo SKU (`AppleAppSku`).
- Il parser legge le colonne per nome; per i rimborsi gli importi sono unità × |prezzo| (Apple scrive il prezzo negativo ma il ricavo positivo).
- Valute: conversione in euro con il cambio BCE del giorno (o fino a 7 giorni prima); un report senza cambi disponibili aspetta. Valute non quotate dalla BCE → `HasUnconvertedAmounts`.
- La firma dei JWT usa `AppStoreConnectGateway.UncachedSigning`: con la cache di IdentityModel il secondo token con la stessa chiave usava un oggetto già eliminato.
- La dashboard (`GET /orgs/{orgId}/metrics?days=&projectId=`) fa finire il periodo all'ultimo giorno con dati, non a oggi. Nel frontend `components/Dashboard.tsx` e `TrendChart.tsx` (barre impilate per store, un solo valore alla volta: mai due assi). I colori `--ios`/`--android` sono validati per contrasto e daltonismo in entrambi i temi: se cambiano, vanno rivalidati.

### Auth
Access token JWT (15 min) nel corpo della risposta, tenuto solo in memoria dal frontend (`src/api/client.ts`); refresh token (30 giorni, ruotato a ogni uso, rilevamento del riuso) in un cookie HttpOnly `SameSite=Strict` con path `/api/v1/auth`. Per questo frontend e API devono stare sullo stesso dominio (Caddy in produzione, proxy di Vite in sviluppo), e il frontend deve avere un solo refresh in volo per volta. Un cambio password ruota `SecurityStamp`, controllato a ogni richiesta in `OnTokenValidated`.

2FA TOTP (RFC 6238, implementazione propria in `Auth/Totp.cs`): il seme è cifrato in colonna con `FieldProtector` (stesse chiavi master del vault). Con la 2FA attiva il login risponde **202** con una `LoginChallenge` (a database, 5 tentativi) da completare su `/auth/login/2fa`; ogni intervallo TOTP vale una volta sola (`TotpLastUsedStep`). Codici di recupero salvati come hash SHA-256.

Inviti: `Invitation` e `Membership` **non** sono `ITenantOwned` (chi accetta non è ancora nel tenant), quindi in `MemberEndpoints` si filtra per tenant a mano. L'invito è nominativo (l'email deve coincidere) e, se ci si registra dal link, vale come conferma dell'email.

### Billing
`IBillingProvider` con `ManualBillingProvider`, che attiva subito qualsiasi piano e annulla subito. Le rotte di abbonamento e fatturazione sono in `Features/Billing` (pagina `/o/:orgId/billing` nel pannello); `BillingProfile` è l'intestatario delle fatture, con i campi della fattura elettronica italiana (SDI o PEC obbligatori per un'azienda IT con partita IVA). Le rotte di billing non richiedono un abbonamento attivo: è da lì che lo si riattiva. I piani sono codice (`Plans`), non dati. Le modifiche richiedono `.RequireActiveSubscription()`; il limite di progetti si controlla in `ProjectEndpoints.Create`.

### Errori
Lancia `ApiProblem` (stato + `code` stabile + dettaglio). `ProblemExceptionHandler` lo trasforma in Problem Details e mappa anche le chiavi non valide (400), gli errori degli store (502) e le violazioni di vincoli unique (409). Il frontend ragiona sul `code`, non sul testo.

## Deploy
`deploy/`: Compose con Caddy (TLS + file statici del frontend + CSP), API, worker e PostgreSQL su una macchina sola. Volumi da salvare: database, `credential-data` (chiavi cifrate) e `report-data` (report grezzi). La chiave master sta in `deploy/keys/` montata in sola lettura e va salvata in un backup **separato** da quello dei dati. `deploy/postgres/10-app-role.sh` crea il ruolo applicativo non superutente.
