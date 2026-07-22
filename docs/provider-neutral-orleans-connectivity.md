# Provider-neutral Orleans client connectivity

Trusted backend applications can remain full Orleans clients without referencing the Host's SQL Server or Azure Storage clustering package. The application uses an authenticated FabrCore Host endpoint to discover the cluster identity and active Orleans gateways, then Orleans continues to provide grain references, observers, streams, serialization, routing, retries, and reconnection.

## Host configuration

Gateway discovery is disabled by default. Enable it and name an ASP.NET Core authorization policy:

```json
{
  "FabrCore": {
    "Host": {
      "GatewayDiscovery": {
        "Enabled": true,
        "AuthorizationPolicy": "FabrCoreGatewayDiscovery",
        "RefreshPeriod": "00:00:30",
        "RequireOrleansTls": true,
        "AdvertisedGateways": []
      }
    }
  }
}
```

Configure authentication and the named policy in the Host application. Authentication middleware must run before FabrCore endpoints. For the simple `AddFabrCoreServer` path, use the post-provider callback to configure Orleans transport TLS without calling `UseOrleans` twice:

```csharp
builder.Services.AddAuthentication(/* application scheme */);
builder.Services.AddAuthorization(options =>
    options.AddPolicy("FabrCoreGatewayDiscovery", policy =>
        policy.RequireAuthenticatedUser()));

builder.AddFabrCoreServer(
    new FabrCoreServerOptions()
        .ConfigureOrleans(orleans =>
            orleans.UseTls(/* Host certificate configuration */)));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseFabrCoreServer();
```

`GET /fabrcoreapi/cluster/gateways` returns only the version, cluster and service IDs, gateway URIs, refresh period, and Orleans TLS requirement. Explicit `AdvertisedGateways` take precedence; otherwise FabrCore derives gateways from active Orleans membership and the configured gateway port. Use explicit addresses behind NAT, load balancers, or when each silo advertises a different public endpoint.

## Client configuration

Reference `FabrCore.Client.Orleans` and configure a dedicated authenticated `HttpClient`. FabrCore uses the client but does not acquire or own bearer tokens, client certificates, or identity-provider secrets.

```csharp
using FabrCore.Client.Orleans;

var discoveryHttpClient = CreateAuthenticatedDiscoveryClient();

await builder.AddFabrCoreOrleansClientAsync(
    discoveryHttpClient,
    options =>
    {
        options.FabrCoreHostUrl = builder.Configuration["FabrCoreHostUrl"]!;
    },
    orleans =>
    {
        orleans.UseTls(/* application certificate configuration */);
    });
```

The application receives Orleans' normal singleton:

```csharp
public sealed class ClientService(IClusterClient clusterClient)
{
    // Grain references, object-reference observers, streams, and the rest of
    // the Orleans client API remain available.
}
```

Initial discovery failures stop startup. Later transient refresh failures retain the last valid gateway list and log a warning. A successful later refresh replaces the cache so Orleans can reconnect through new gateways.

Gateway discovery authenticates only the HTTP request; it does not secure the subsequent Orleans connection. Keep gateways on private networking and use Orleans mTLS in production. `AllowInsecureOrleansTransport` is an explicit development-only opt-in for a Host which advertises `RequireOrleansTls: false`.
