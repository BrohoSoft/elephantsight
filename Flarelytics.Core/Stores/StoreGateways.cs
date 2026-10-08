using Flarelytics.Core.Database.Entities;

namespace Flarelytics.Core.Stores;

/// <summary>Sceglie il gateway giusto per lo store di una credenziale.</summary>
public class StoreGateways(IEnumerable<IStoreGateway> gateways)
{
    private readonly Dictionary<Store, IStoreGateway> _byStore = gateways.ToDictionary(g => g.Store);

    public IStoreGateway For(Store store) =>
        _byStore.TryGetValue(store, out var gateway)
            ? gateway
            : throw new InvalidOperationException($"Nessun gateway registrato per {store}.");
}
