# ElephantSight

Le tue app su App Store e Google Play in un pannello solo, iOS e Android affiancati: download e paesi, versioni e build, recensioni con risposta, testi e screenshot della pagina dello store, file di firma cifrati, caricamento delle build, e un calendario per programmare i post su Bluesky, Mastodon, Instagram (anche Reel), Pagine Facebook, TikTok e Threads, anche ricorrenti (ogni giorno, certi giorni della settimana, una volta al mese). Self-hosted: gira sul tuo server, con le tue chiavi.

## Le chiavi degli store

| Store | Cosa serve | Per cosa |
|---|---|---|
| App Store | Chiave API **del team** (.p8) con ruolo **Admin** (o App Manager + una chiave Sales), Issuer ID, Vendor Number | download, versioni, build, recensioni, pagina dello store, caricamento .ipa |
| Google Play | JSON di un **service account**, invitato in Play Console con i permessi di rilascio, gestione della scheda, risposta alle recensioni e report in blocco; la **Google Play Android Developer API** abilitata nel progetto Cloud; l'URI del bucket dei report | download, release, recensioni, pagina dello store, caricamento .aab |

## Installazione

Servono Docker e Docker Compose.

```bash
git clone <repository> flarelytics && cd flarelytics
cat > .env <<EOF
DB_PASSWORD=una-password-lunga-e-casuale
PUBLIC_URL=https://flarelytics.tuodominio.it
EOF
docker compose up -d
```

Apri `PUBLIC_URL` e **completa subito l'installazione**: il primo che apre la pagina crea l'account amministratore. Poi attiva la verifica in due passaggi dal tuo account. Gli altri utenti entrano solo su invito.

### HTTPS
Mettilo dietro un proxy con HTTPS (Caddy, Traefik, nginx…) che inoltra alla porta `8080`. Se lo usi in HTTP da un indirizzo che non è `localhost`, aggiungi `SECURE_COOKIES=false` al file `.env`, altrimenti il browser non conserva la sessione.

### Caricamento delle build
Le build passano dal pannello: se usi un proxy davanti, alza il limite della dimensione delle richieste (nginx `client_max_body_size 4g;`, Caddy di suo non ne ha).

### Impostazioni dell'istanza
Chi fa l'installazione è **amministratore dell'istanza**: dal menu utente apre *Impostazioni dell'istanza* e inserisce SMTP e chiavi delle app social senza toccare `.env` né riavviare. I valori sono cifrati; password e secret, una volta salvati, non si rileggono più. Quello che si mette dal pannello vince sul `.env`, che resta come alternativa. Da lì si nominano anche altri amministratori dell'istanza (separati dai ruoli delle organizzazioni).

### Email (facoltativa)
Senza SMTP gli inviti si mandano copiando il link dal pannello, e il recupero password non c'è. Si configura da *Impostazioni dell'istanza* (con un'email di prova), oppure in `.env` con `SMTP_HOST`, `SMTP_PORT`, `SMTP_USERNAME`, `SMTP_PASSWORD`, `SMTP_FROM`.

### Social (facoltativo)
Bluesky e Mastodon si collegano dal pannello (Account social) con una password per app o un token. Per Instagram e le Pagine Facebook serve un'app Meta della tua installazione (il pannello spiega come crearla). Due modi, anche insieme: con l'accesso a Facebook (Pagine, e Instagram collegati a una Pagina) aggiungi a `.env` `META_APP_ID` e `META_APP_SECRET`; con l'accesso a Instagram (account Instagram professionali senza Pagina) `INSTAGRAM_APP_ID` e `INSTAGRAM_APP_SECRET`, cioè l'Instagram App ID del prodotto Instagram, non quello dell'app Meta. Instagram scarica le immagini da `PUBLIC_URL`, che quindi deve essere raggiungibile da internet: se davanti c'è Cloudflare Access, lascia libero `/api/v1/social/media/` (gli indirizzi sono firmati e scadono dopo un'ora).

Per TikTok serve un'app su developers.tiktok.com (Login Kit e Content Posting API): metti `TIKTOK_CLIENT_KEY` e `TIKTOK_CLIENT_SECRET` in `.env`. Finché TikTok non approva l'app, i video escono solo privati su account privati.

Per Threads serve un'app Meta con il caso d'uso "Access the Threads API": metti in `.env` `THREADS_APP_ID` e `THREADS_APP_SECRET`, cioè il Threads App ID e il suo secret (nelle impostazioni del caso d'uso), non quelli dell'app. Come Instagram, Threads scarica immagini e video da `PUBLIC_URL`.

I **post ricorrenti** (Social → Post ricorrenti) escono da soli secondo una regola: ogni N giorni, certi giorni della settimana ogni N settimane, o un giorno del mese ogni N mesi, a un'ora del fuso di chi li crea. Ogni uscita diventa un post del calendario, con una copia di immagini e video (un video quotidiano occupa spazio ogni giorno). Un'uscita mancata di più di un'ora perché il server era spento si salta.

### Progetti, membri e permessi
Ogni membro ha un ruolo (lettore, admin, owner) e vede tutti i progetti o solo alcuni, con le sezioni Store e/o Social: un cliente può vedere solo il suo progetto, e solo il calendario. Si sceglie quando lo inviti e si cambia da Membri. Gli account social sono dell'organizzazione e si collegano a uno o più progetti (Account social → Cambia progetti): lo stesso account può pubblicare per un'app e per te, con post diversi. Il calendario generale mostra tutto quello che vedi, quello del progetto solo i suoi post.

### Chiavi API (facoltative)
Da Organizzazione → Chiavi API crei una chiave per ogni programma (un CMS, uno script, un'automazione). Con quella il programma manda post (testo, immagine o carosello in JPEG, data di pubblicazione), anche molti in una chiamata, su `/api/v1/public`: finiscono nella coda **Da programmare**, dove scegli account e ora. La chiave da sola non pubblica niente. La pagina delle chiavi ha gli esempi con `curl`.

## Backup

| Cosa | Dove | Perché |
|---|---|---|
| Database | volume `postgres-data` (`docker compose exec postgres pg_dump -U postgres -Fc flarelytics > flarelytics.dump`) | utenti, progetti, metriche |
| **Chiavi** | volume `flarelytics-keys` | senza la chiave master le credenziali degli store non si decifrano più |
| Credenziali cifrate | volume `flarelytics-data` | |
| Report scaricati e immagini dei post | volume `flarelytics-reports` | gli store non tengono lo storico per sempre: sono l'unica copia del passato |

Salva le **chiavi in un posto diverso** dal resto: se finiscono nello stesso backup, chi lo ruba ha anche la chiave per aprirlo.

### Rotazione della chiave master
Aggiungi `v2.key` nel volume delle chiavi, imposta `Secrets__ActiveKeyVersion=v2` e riavvia; ricifra i file esistenti con `SecretVault.RewrapAsync` (il comando non c'è ancora); quando nessuna credenziale usa più `v1` (colonna `KeyVersion`), togli `v1.key`.

## Aggiornamento

```bash
git pull && docker compose up -d --build
```

Le migrazioni del database si applicano da sole all'avvio.

## Sviluppo

Servono .NET 10, Node 22 e Docker.

```bash
docker compose -f compose.dev.yaml up -d          # PostgreSQL su :5433
cd Flarelytics.Api && dotnet run                  # API + sincronizzazione su :5080 (Scalar su /scalar)
cd Flarelytics.Web && npm install && npm run dev  # pannello su :5173
dotnet test                                       # serve Docker (Testcontainers)
```

In sviluppo le email non partono: i link si leggono nel log dell'API.

## Licenza

ElephantSight è distribuito con la [Elastic License 2.0](LICENSE): il codice è pubblico e puoi usarlo, modificarlo e installarlo gratis, anche in azienda, per gestire le tue app e i tuoi social. Non puoi offrirlo ad altri come servizio gestito o a pagamento, né togliere o aggirare le parti che lo proteggono. È una licenza *source-available*, non open source in senso stretto.

ElephantSight is licensed under the [Elastic License 2.0](LICENSE): free to use, modify and self-host, including for your own business; you may not provide it to others as a managed service.

