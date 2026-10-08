using Flarelytics.Core;
using Flarelytics.Worker;

// Il worker: scarica i report dagli store e li trasforma in metriche. Non
// espone porte e non applica le migration, che sono compito dell'API: parte
// dopo di lei, e in sviluppo va avviato con l'API già su.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddFlarelyticsDatabase(builder.Configuration);
builder.Services.AddFlarelyticsSecretsAndStores(builder.Configuration, createDevelopmentKey: builder.Environment.IsDevelopment());
builder.Services.AddFlarelyticsSync();
builder.Services.AddHostedService<SyncWorker>();

builder.Build().Run();
