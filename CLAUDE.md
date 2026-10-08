# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Cos'è

Flarelytics è uno strumento **self-hosted** (un'immagine Docker + PostgreSQL) per gestire le proprie app su App Store e Google Play da un pannello solo. Un'istanza ha più organizzazioni (tenant); un'organizzazione ha più progetti; ogni progetto collega al massimo un'app App Store e un'app Google Play, attraverso le chiavi degli store caricate dall'utente (il .p8 di Apple, il JSON di un service account Google). Non c'è registrazione libera: il primo utente lo crea l'installer (`/setup`, finché non esistono utenti), gli altri entrano per invito. Niente pagamenti né fatturazione.

Stato: fatti account con 2FA, organizzazioni con membri e inviti, chiavi cifrate, dashboard dei download App Store + Google Play, icone. Piano delle prossime fasi: versioni e build (B), recensioni unificate (C), gestione della pagina dello store (D), cassaforte dei file di firma (E), caricamento delle build (F). Essendo self-hosted, chi installa usa le API per le proprie app: è l'uso consentito dai termini di Apple e Google (compresa la Publishing API di Google, che prima andava evitata).

Codice, commenti e messaggi d'errore sono in italiano; i commenti spiegano il *perché*. Mantieni lo stesso stile.

## Comandi

```bash
docker compose -f compose.dev.yaml up -d                          # PostgreSQL di sviluppo su :5433
cd Flarelytics.Api && dotnet run                                   # API + sincronizzazione su http://localhost:5080, Scalar su /scalar
cd Flarelytics.Web && npm install && npm run dev                   # pannello su http://localhost:5173 (proxy /api → 5080)
cd Flarelytics.Web && npm run build                                # typecheck + build di produzione
docker compose up -d --build                                       # l'installazione vera (immagine unica + PostgreSQL), con .env: DB_PASSWORD, PUBLIC_URL
dotnet build
dotnet test                                                        # serve Docker (Testcontainers)
dotnet test --filter "FullyQualifiedName~CredentialTests"          # una classe
dotnet test --filter "FullyQualifiedName~SecretVaultTests.Un_byte_alterato_si_riconosce"   # un test
dotnet ef migrations add <Nome> --project Flarelytics.Core --startup-project Flarelytics.Core --output-dir Database/Migrations
```

In sviluppo le email non partono: i link si leggono nel log dell'API. L'SMTP è facoltativo anche in produzione (`/api/v1/instance` dice al pannello se c'è): senza, gli inviti restituiscono il link da copiare. La chiave master (e quella dei JWT se `Jwt:Key` manca) si crea da sola in `Secrets:KeysDirectory` con `Secrets:CreateKeyIfMissing` (attivo in Development e nell'immagine Docker). `dotnet run --environment` non imposta l'ambiente: usa il launch profile oppure `ASPNETCORE_ENVIRONMENT=Development`.

## Architettura

- **Flarelytics.Core**: modello EF, isolamento dei tenant, cifratura, client degli store, sincronizzazione (`Sync/`, con `SyncWorker` che chiama `SyncCoordinator.RunOnceAsync` ogni `Sync:PollInterval`).
- **Flarelytics.Api**: minimal API sotto `/api/v1`, un file per feature in `Features/` (handler statici, richieste, validatori FluentValidation nello stesso file). Ospita anche la sincronizzazione (`Worker:Enabled`, spenta nei test) e, nell'immagine Docker, serve il pannello da `wwwroot` (`Common/Frontend.cs`, che mette anche gli header di sicurezza/CSP). Deve girare in **una sola istanza**.
- **Flarelytics.Tests**: integrazione con Testcontainers (un database per test) e unit test. `MigrationTests` applica le migration una per volta sopra dati già presenti: una colonna NOT NULL nuova vuole un valore predefinito, altrimenti in produzione l'API non parte.
- **Flarelytics.Web**: React 19 + Vite + Tailwind 4 + TanStack Query + React Router, stile Supabase con tema chiaro, scuro o di sistema: i colori sono variabili CSS in `src/styles.css` (`@theme inline` → `var(--…)`, una palette per `[data-theme]`); `public/theme.js` applica il tema prima del primo disegno (file e non script inline, per la CSP) e `src/theme.ts` lo gestisce dopo. Per un colore nuovo serve un valore in entrambe le palette. Componenti base in `src/components/ui.tsx`, tipi delle risposte scritti a mano in `src/api/types.ts` (da allineare ai record C#). In produzione lo serve l'API (il `Dockerfile` alla radice lo compila e lo copia in `wwwroot`).

### Isolamento dei tenant: due livelli, entrambi obbligatori
1. Le entità `ITenantOwned` ricevono un filtro globale EF su `TenantContext.TenantId` (`FlarelyticsDbContext`). Con il tenant non impostato le tabelle appaiono vuote.
2. Row-Level Security di PostgreSQL. `TenantConnectionInterceptor` scrive `app.tenant_id` a ogni apertura di connessione; le politiche sono create in migration con `RowLevelSecurity.Enable(...)`. **Una nuova tabella `ITenantOwned` ha bisogno della sua chiamata `RowLevelSecurity.Enable` nella migration**: non è automatico.

`TenantContext.Set` si chiama solo in `OrgAccessFilter` (tutte le rotte `/orgs/{orgId}/...`, che controllano la membership e il ruolo) e nel worker, tenant per tenant. Se la richiesta usa una transazione esplicita, il tenant va impostato prima di aprirla. L'app deve collegarsi con un ruolo **non superutente e senza BYPASSRLS**, proprietario delle tabelle: per questo le politiche sono `FORCE`. I test fanno lo stesso (`PostgresFixture`), altrimenti i test sulla RLS passerebbero senza verificare niente.

`Tenant`, `User`, `Membership`, `RefreshToken`, `EmailToken` non sono del tenant: si leggono prima di aver scelto un tenant.

### Credenziali degli store
- Metadati (Key ID, Issuer ID, email del service account, bucket) in `StoreCredential`; il segreto vero solo cifrato su disco tramite `SecretVault`. Nessuna rotta restituisce il segreto.
- Cifratura a busta: una DEK casuale per file (AES-256-GCM), cifrata con la chiave master (KEK) versionata letta da `Secrets:KeysDirectory`. Tenant e credenziale entrano come dati associati, quindi un file spostato non si decifra. Rotazione: nuova chiave + `ActiveKeyVersion` + `SecretVault.RewrapAsync`.
- Il segreto si usa solo tramite `CredentialSecrets.UseAsync`, che lo azzera dopo l'uso.
- Al caricamento: parse (`AppleKey`/`GoogleServiceAccount`) → verifica con lo store (`IStoreGateway`) → cifratura → salvataggio. `Rejected` non si salva; `Limited` sì, con l'avviso (i permessi di Google Play arrivano ore dopo l'invito).
- Gateway senza SDK: JWT ES256 firmato da noi per Apple; JWT RS256 scambiato con un token OAuth per Google (`GoogleAuth`).
- Google: verifica della chiave ed elenco delle app passano dal bucket GCS `pubsite_prod_…` (le app dai nomi dei file). Il codice è nato quando Flarelytics doveva essere un SaaS e i termini della Play Developer Reporting/Publishing API vietavano di usarle con l'account di un terzo; da self-hosted quelle API si possono usare (servono per build, listing, recensioni).
- `ProjectApp.ExternalAppId` è l'Apple ID numerico (non il bundle id) oppure il package name Android.

### Sincronizzazione e metriche (fase 2)
- Flusso: `SyncCoordinator` → cambi BCE (`RatesRefresher`) → per ogni tenant apre uno scope, imposta `TenantContext` (la RLS vale anche nel worker) e chiama `AppleSalesSync` per le credenziali App Store da sincronizzare (ogni `Sync:Interval`, o subito con "Sincronizza ora" = `SyncRequestedAtUtc`).
- `AppleSalesSync` scarica SALES/SUMMARY/DAILY versione `1_0` per ogni giorno mancante degli ultimi `BackfillDays` (dal più recente), e ritenta i giorni vuoti recenti: Apple risponde 404 sia per "nessuna vendita" sia per "non ancora pronto". I giorni seguono il fuso del Pacifico (`AppleCalendar`). 401 rende la chiave `Invalid`, 403 `Limited`.
- Google Play: `GooglePlaySync` legge `stats/installs/installs_<package>_<aaaamm>_country.csv` (UTF-16, colonne per nome con alternative; Download = "Daily User Installs"), riscarica un file mensile solo se cambia l'MD5, e riscrive solo le colonne delle installazioni (download, aggiornamenti, disinstallazioni).
- **Niente ricavi, per scelta di prodotto**: si mostrano solo download, riscaricamenti, aggiornamenti e disinstallazioni. I report finanziari di Google (vendite, guadagni) non si scaricano. Le colonne dei ricavi in `DailyAppMetric` si riempiono ancora per Apple (vengono dallo stesso report dei download), ma l'API non le espone come coperte (`StoreCoverage`) e la UI non le mostra. Per i download Apple basta una chiave con ruolo **Sales**.
- Icone (`AppIcon`, tabella globale, non del tenant): il worker le scarica da iTunes Lookup (Apple, nei paesi con più download dell'app) e dall'`og:image` della pagina pubblica di Google Play. Servite anonime da `GET /api/v1/icons/{id}` con un Guid casuale, per non rivelare quali app sono seguite.
- I report grezzi (gzip, così come arrivano) stanno su disco tramite `ReportStorage` e sono la fonte di verità: `ReportProcessor` ne ricava `DailyAppMetric` senza rete. Cambiando il modo di contare si alza `AppleSalesAggregator.Version` e i file vengono rielaborati al giro successivo.
- `DailyAppMetric` (schema `metrics`, chiave tenant+store+app+giorno+paese, importi in micro-euro) è legata all'app, non al progetto: i progetti la leggono tramite `ProjectApp.ExternalAppId`. Gli acquisti in-app si attribuiscono all'app tramite lo SKU (`AppleAppSku`).
- Il parser legge le colonne per nome; per i rimborsi gli importi sono unità × |prezzo| (Apple scrive il prezzo negativo ma il ricavo positivo).
- Valute: conversione in euro con il cambio BCE del giorno (o fino a 7 giorni prima); un report senza cambi disponibili aspetta. Valute non quotate dalla BCE → `HasUnconvertedAmounts`.
- La firma dei JWT usa `AppStoreConnectGateway.UncachedSigning`: con la cache di IdentityModel il secondo token con la stessa chiave usava un oggetto già eliminato.
- La dashboard (`GET /orgs/{orgId}/metrics?days=&projectId=`) fa finire il periodo all'ultimo giorno con dati, non a oggi. Nel frontend `components/Dashboard.tsx` e `TrendChart.tsx` (barre impilate per store, un solo valore alla volta: mai due assi). I colori `--ios`/`--android` sono validati per contrasto e daltonismo in entrambi i temi: se cambiano, vanno rivalidati.

### Auth
Access token JWT (15 min) nel corpo della risposta, tenuto solo in memoria dal frontend (`src/api/client.ts`); refresh token (30 giorni, ruotato a ogni uso, rilevamento del riuso) in un cookie HttpOnly `SameSite=Strict` con path `/api/v1/auth` (`Secure` salvo `Auth:SecureCookies=false`, necessario in HTTP fuori da localhost). Per questo frontend e API stanno sulla stessa origine (l'API serve il pannello in produzione, proxy di Vite in sviluppo), e il frontend deve avere un solo refresh in volo per volta. Un cambio password ruota `SecurityStamp`, controllato a ogni richiesta in `OnTokenValidated`.

2FA TOTP (RFC 6238, implementazione propria in `Auth/Totp.cs`): il seme è cifrato in colonna con `FieldProtector` (stesse chiavi master del vault). Con la 2FA attiva il login risponde **202** con una `LoginChallenge` (a database, 5 tentativi) da completare su `/auth/login/2fa`; ogni intervallo TOTP vale una volta sola (`TotpLastUsedStep`). Codici di recupero salvati come hash SHA-256.

Installer (`Features/Setup`): `GET /instance` (`setupRequired`, `emailEnabled`) e `POST /setup`, che crea l'amministratore e la prima organizzazione sotto un `pg_advisory_xact_lock`, così due installer simultanei non creano due amministratori. Il pannello reindirizza tutto a `/setup` finché serve.

Inviti: `Invitation` e `Membership` **non** sono `ITenantOwned` (chi accetta non è ancora nel tenant), quindi in `MemberEndpoints` si filtra per tenant a mano. L'invito è nominativo (l'email deve coincidere), vale come conferma dell'email, ed è l'unico modo di registrarsi (`POST /auth/register` vuole sempre il token). La creazione restituisce il link una volta sola, per mandarlo a mano senza SMTP.

### Errori
Lancia `ApiProblem` (stato + `code` stabile + dettaglio). `ProblemExceptionHandler` lo trasforma in Problem Details e mappa anche le chiavi non valide (400), gli errori degli store (502) e le violazioni di vincoli unique (409). Il frontend ragiona sul `code`, non sul testo.

## Deploy
`Dockerfile` e `compose.yaml` alla radice: un'immagine sola (pannello + API + sincronizzazione, porta 8080, dati in `/data`) e PostgreSQL. Volumi: `flarelytics-keys` (chiave master e chiave dei JWT: da salvare **a parte** dai dati), `flarelytics-data` (credenziali cifrate), `flarelytics-reports` (report grezzi e icone), `postgres-data`. `deploy/postgres/10-app-role.sh` crea il ruolo applicativo non superutente: senza, la RLS non varrebbe.
