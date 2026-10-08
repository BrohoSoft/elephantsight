using Flarelytics.Core.Database.Entities;
using Flarelytics.Core.Reports;
using Flarelytics.Core.Stores;
using Flarelytics.Tests.Integration.Infrastructure;
using static Flarelytics.Tests.Integration.Infrastructure.GoogleReports;

namespace Flarelytics.Tests.Unit;

/// <summary>La verifica e l'elenco delle app di Google, fatti solo con il bucket dei report.</summary>
public class GooglePlayGatewayTests
{
    private static readonly DateOnly Month = new(2026, 9, 1);
    private readonly FakeGooglePlayReports _bucket = new();

    private static StoreCredential Credential(string? bucket = "pubsite_prod_rev_0123") =>
        StoreCredential.ForGooglePlay(Guid.NewGuid(), "Play", "fp", "sa@progetto.iam.gserviceaccount.com", bucket);

    private GooglePlayGateway Gateway => new(_bucket);

    [Fact]
    public async Task Con_i_report_nel_bucket_la_chiave_e_valida()
    {
        _bucket.Files[Name("com.app", Month)] = Csv("com.app");
        Assert.Equal(VerificationOutcome.Ok, (await Gateway.VerifyAsync(Credential(), default, default)).Outcome);
    }

    [Fact]
    public async Task Una_chiave_che_google_rifiuta_non_si_salva()
    {
        _bucket.Fail = new GoogleAccessException(GoogleAccessProblem.InvalidKey, "chiave revocata");
        Assert.Equal(VerificationOutcome.Rejected, (await Gateway.VerifyAsync(Credential(), default, default)).Outcome);
    }

    [Fact]
    public async Task Senza_bucket_o_senza_permesso_e_parziale_e_dice_cosa_fare()
    {
        var noBucket = await Gateway.VerifyAsync(Credential(bucket: null), default, default);
        Assert.Equal(VerificationOutcome.Limited, noBucket.Outcome);
        Assert.Contains("bucket", noBucket.Message);

        _bucket.Fail = new GoogleAccessException(GoogleAccessProblem.NoBucketAccess, "403");
        var notInvited = await Gateway.VerifyAsync(Credential(), default, default);
        Assert.Equal(VerificationOutcome.Limited, notInvited.Outcome);
        Assert.Contains("sa@progetto.iam.gserviceaccount.com", notInvited.Message);
    }

    [Fact]
    public async Task Un_bucket_senza_report_di_installazioni_e_parziale()
    {
        _bucket.Files["stats/installs/installs_com.app_202609_device.csv"] = Csv("com.app");
        Assert.Equal(VerificationOutcome.Limited, (await Gateway.VerifyAsync(Credential(), default, default)).Outcome);
    }

    [Fact]
    public async Task Le_app_sono_quelle_che_hanno_report_nel_bucket()
    {
        _bucket.Files[Name("com.zeta", Month)] = Csv("com.zeta");
        _bucket.Files[Name("com.alfa.mia_app", Month)] = Csv("com.alfa.mia_app");
        _bucket.Files[Name("com.alfa.mia_app", Month.AddMonths(-1))] = Csv("com.alfa.mia_app");

        var apps = await Gateway.ListAppsAsync(Credential(), default, default);

        Assert.Equal(["com.alfa.mia_app", "com.zeta"], apps.Select(a => a.ExternalId));
    }
}
