# Flarelytics

Le tue app su App Store e Google Play in un pannello solo: download, aggiornamenti, disinstallazioni, paesi, con iOS e Android affiancati. Self-hosted: gira sul tuo server, con le tue chiavi.

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

### Email (facoltativa)
Senza SMTP gli inviti si mandano copiando il link dal pannello, e il recupero password non c'è. Per attivarla aggiungi a `.env` `SMTP_HOST`, `SMTP_PORT`, `SMTP_USERNAME`, `SMTP_PASSWORD`, `SMTP_FROM`.

## Backup

| Cosa | Dove | Perché |
|---|---|---|
| Database | volume `postgres-data` (`docker compose exec postgres pg_dump -U postgres -Fc flarelytics > flarelytics.dump`) | utenti, progetti, metriche |
| **Chiavi** | volume `flarelytics-keys` | senza la chiave master le credenziali degli store non si decifrano più |
| Credenziali cifrate | volume `flarelytics-data` | |
| Report scaricati | volume `flarelytics-reports` | gli store non tengono lo storico per sempre: sono l'unica copia del passato |

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
