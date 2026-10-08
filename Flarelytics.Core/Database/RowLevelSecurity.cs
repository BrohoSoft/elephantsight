using Flarelytics.Core.Tenancy;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Flarelytics.Core.Database;

/// <summary>
/// Accende la Row-Level Security sulle tabelle dei tenant, dalle migration.
/// </summary>
/// <remarks>
/// <para>È la seconda linea di difesa. La prima è il filtro globale di EF, che
/// però protegge solo le query scritte con EF: un <c>FromSql</c>, un
/// <c>IgnoreQueryFilters</c> di troppo o uno script di manutenzione lo
/// scavalcano. La politica invece la applica il database a chiunque.</para>
///
/// <para><b>FORCE</b> perché l'applicazione si collega con il ruolo
/// proprietario delle tabelle (lo stesso che esegue le migration), e senza
/// FORCE il proprietario è esentato. Resta esentato solo un superutente: per
/// questo l'applicazione non deve mai collegarsi come <c>postgres</c>.</para>
///
/// <para>Con il tenant vuoto <c>nullif</c> produce NULL, il confronto non è
/// mai vero e la tabella appare vuota.</para>
/// </remarks>
public static class RowLevelSecurity
{
    public const string PolicyName = "tenant_isolation";

    public static void Enable(MigrationBuilder migration, string table, string schema = "public")
    {
        const string condition =
            $"\"TenantId\" = nullif(current_setting('{TenantConnectionInterceptor.SettingName}', true), '')::uuid";

        migration.Sql($"""
            ALTER TABLE "{schema}"."{table}" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "{schema}"."{table}" FORCE ROW LEVEL SECURITY;
            CREATE POLICY {PolicyName} ON "{schema}"."{table}" USING ({condition}) WITH CHECK ({condition});
            """);
    }

    public static void Disable(MigrationBuilder migration, string table, string schema = "public")
    {
        migration.Sql($"""
            DROP POLICY IF EXISTS {PolicyName} ON "{schema}"."{table}";
            ALTER TABLE "{schema}"."{table}" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE "{schema}"."{table}" DISABLE ROW LEVEL SECURITY;
            """);
    }
}
