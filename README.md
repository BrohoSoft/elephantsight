# Flarelytics

App Store Connect e Google Play in un'unica dashboard.

## Sviluppo

Servono .NET 10, Node 22 e Docker.

```bash
docker compose -f compose.dev.yaml up -d
cd Flarelytics.Api && dotnet run          # in un terminale
cd Flarelytics.Web && npm install && npm run dev   # in un altro
cd Flarelytics.Worker && dotnet run                # in un terzo, per scaricare i dati dagli store
```

Il pannello è su http://localhost:5173, l'API su http://localhost:5080 (documentazione su /scalar). Le email (conferma, reset, inviti) non partono: i link si leggono nel log dell'API.

Test: `dotnet test` (i test di integrazione avviano PostgreSQL con Testcontainers).

## Produzione

Su una macchina Linux con Docker, dalla cartella `deploy/`:

1. `cp .env.example .env` e compila le variabili.
2. **Chiave master**, una volta sola:
   ```bash
   mkdir -p keys && openssl rand -base64 32 > keys/v1.key
   sudo chown -R 10001 keys && sudo chmod 500 keys && sudo chmod 400 keys/v1.key
   ```
   Salvane una copia **fuori dal server e separata dai backup dei dati**. Senza questa chiave le credenziali dei clienti non si decifrano più; se finisce insieme al backup, la cifratura non protegge niente.
3. `docker compose up -d --build`

### Backup
- Database: `docker compose exec postgres pg_dump -U postgres -Fc flarelytics > flarelytics.dump`
- Credenziali cifrate: il volume `credential-data`.
- Report grezzi degli store: il volume `report-data`. Gli store non tengono lo storico per sempre (Apple circa un anno): questi file sono l'unica copia di quello che è più vecchio.
- Chiave master: a parte (vedi sopra).

### Rotazione della chiave master
Aggiungi `keys/v2.key`, imposta `MASTER_KEY_VERSION=v2` e riavvia; ricifra i file esistenti con `SecretVault.RewrapAsync` (il comando arriverà con il worker); quando nessuna credenziale usa più `v1` (colonna `KeyVersion`), togli `v1.key`.
