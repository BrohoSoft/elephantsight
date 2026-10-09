# WatchStore

Le tue app su App Store e Google Play in un pannello solo, iOS e Android affiancati: download e paesi, versioni e build, recensioni con risposta, testi e screenshot della pagina dello store, file di firma cifrati, caricamento delle build, e un calendario per programmare i post su Bluesky, Mastodon, Instagram e Pagine Facebook. Self-hosted: gira sul tuo server, con le tue chiavi.

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

### Email (facoltativa)
Senza SMTP gli inviti si mandano copiando il link dal pannello, e il recupero password non c'è. Per attivarla aggiungi a `.env` `SMTP_HOST`, `SMTP_PORT`, `SMTP_USERNAME`, `SMTP_PASSWORD`, `SMTP_FROM`.

### Social (facoltativo)
Bluesky e Mastodon si collegano dal pannello (Account social) con una password per app o un token. Per Instagram e le Pagine Facebook serve un'app Meta della tua installazione (il pannello spiega come crearla). Due modi, anche insieme: con l'accesso a Facebook (Pagine, e Instagram collegati a una Pagina) aggiungi a `.env` `META_APP_ID` e `META_APP_SECRET`; con l'accesso a Instagram (account Instagram professionali senza Pagina) `INSTAGRAM_APP_ID` e `INSTAGRAM_APP_SECRET`, cioè l'Instagram App ID del prodotto Instagram, non quello dell'app Meta. Instagram scarica le immagini da `PUBLIC_URL`, che quindi deve essere raggiungibile da internet: se davanti c'è Cloudflare Access, lascia libero `/api/v1/social/media/` (gli indirizzi sono firmati e scadono dopo un'ora).

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
