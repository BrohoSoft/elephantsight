using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Flarelytics.Core.Tenancy;

/// <summary>
/// Scrive il tenant corrente sulla connessione PostgreSQL ogni volta che EF la
/// apre, così che le politiche di Row-Level Security sappiano chi sta leggendo.
/// </summary>
/// <remarks>
/// <para><b>A ogni apertura, non una volta sola.</b> Le connessioni vengono dal
/// pool e prima di questa richiesta hanno servito qualcun altro: il valore va
/// sempre riscritto, anche quando è vuoto, altrimenti resterebbe quello del
/// tenant precedente.</para>
///
/// <para>EF apre e chiude la connessione a ogni operazione, quindi un tenant
/// impostato dopo la prima query vale dalla query successiva. L'eccezione è una
/// transazione esplicita, che tiene la connessione aperta: il tenant va
/// impostato prima di aprirla.</para>
/// </remarks>
public class TenantConnectionInterceptor(TenantContext tenant) : DbConnectionInterceptor
{
    /// <summary>Il nome dell'impostazione letta dalle politiche RLS.</summary>
    public const string SettingName = "app.tenant_id";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();

        // set_config e non SET: accetta un parametro, quindi nessun valore
        // finisce concatenato nel testo SQL. Il terzo argomento a false vuol
        // dire "per la sessione", che è proprio la connessione appena aperta.
        command.CommandText = $"SELECT set_config('{SettingName}', @tenant, false)";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant";
        parameter.Value = tenant.TenantId?.ToString() ?? string.Empty;
        command.Parameters.Add(parameter);

        return command;
    }
}
