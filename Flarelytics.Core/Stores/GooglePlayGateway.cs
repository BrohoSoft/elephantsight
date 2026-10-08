using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Reports;

namespace Flarelytics.Core.Stores;

/// <summary>
/// Google Play, attraverso il bucket dei report in blocco di Play Console.
/// </summary>
/// <remarks>
/// <para><b>Solo Cloud Storage, niente Play Developer Reporting API.</b> I
/// termini di quella API (marzo 2022) vietano di usarla con l'account
/// sviluppatore di qualcun altro: <i>"You will not utilize a third party's
/// Developer Account to access the Play Developer Reporting API"</i>, che è
/// esattamente quello che farebbe Flarelytics con il service account di un
/// cliente. I report in blocco invece si leggono come un utente invitato in
/// Play Console, e bastano per verificare la chiave e per sapere quali app ci
/// sono: ogni app ha i suoi file, con il package name nel nome.</para>
/// </remarks>
public class GooglePlayGateway(IGooglePlayReports reports) : IStoreGateway
{
    public Store Store => Store.GooglePlay;

    public async Task<VerificationResult> VerifyAsync(StoreCredential credential, ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        string token;
        try
        {
            token = await reports.ConnectAsync(secret, ct);
        }
        catch (GoogleAccessException e)
        {
            return new(e.Problem == GoogleAccessProblem.InvalidKey ? VerificationOutcome.Rejected : VerificationOutcome.Unreachable, e.Message);
        }

        // Da qui la chiave è buona: quello che può mancare è l'accesso ai
        // report, che dopo l'invito in Play Console arriva anche con ore di
        // ritardo. Si segnala, non si rifiuta.
        if (credential.GoogleReportsBucket is not { } bucket)
        {
            return new(VerificationOutcome.Limited,
                "Manca il bucket dei report: senza, installazioni e guadagni non si possono scaricare. Lo trovi in Play Console, Scarica report, \"Copia URI di Cloud Storage\".");
        }

        try
        {
            var files = await reports.ListAsync(token, bucket, GoogleInstallsParser.Prefix, ct);

            return files.Any(f => GoogleInstallsParser.ParseName(f.Name) is not null)
                ? VerificationResult.Ok
                : new(VerificationOutcome.Limited,
                    "Il bucket si legge ma non contiene ancora report delle installazioni. Succede con un'app appena pubblicata: Google li aggiunge entro qualche giorno.");
        }
        catch (GoogleAccessException e)
        {
            return new(e.Problem == GoogleAccessProblem.Failed ? VerificationOutcome.Unreachable : VerificationOutcome.Limited,
                e.Problem == GoogleAccessProblem.NoBucketAccess && !e.Message.Contains("non esiste")
                    ? $"Il service account non può ancora leggere il bucket: invita {credential.GoogleClientEmail} in Play Console, Utenti e autorizzazioni, con il permesso di scaricare i report in blocco. Dopo l'invito possono servire alcune ore."
                    : e.Message);
        }
    }

    /// <summary>Le app sono quelle che hanno report nel bucket: il package name è nel nome di ogni file.</summary>
    public async Task<IReadOnlyList<StoreAppInfo>> ListAppsAsync(StoreCredential credential, ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        if (credential.GoogleReportsBucket is not { } bucket)
        {
            throw new StoreAccessException("Aggiungi il bucket dei report alla chiave: è da lì che si leggono le app.");
        }

        try
        {
            var token = await reports.ConnectAsync(secret, ct);
            var files = await reports.ListAsync(token, bucket, GoogleInstallsParser.Prefix, ct);

            return files
                .Select(f => GoogleInstallsParser.ParseName(f.Name)?.Package)
                .OfType<string>()
                .Distinct()
                .Order()
                .Select(p => new StoreAppInfo(p, p, p))
                .ToList();
        }
        catch (GoogleAccessException e)
        {
            throw new StoreAccessException(e.Message);
        }
    }
}
