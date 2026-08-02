# August 2026 repository boundary

## Project ownership

| Project or concern | Owner |
|---|---|
| Core, SDK, Host, Client.Orleans, Orleans providers | OSS |
| `FabrCore.Services.Contracts` | OSS |
| `FabrCore.Services.Memory` and tests | OSS |
| `FabrCore.Services.GraphRag` and tests | OSS |
| `FabrCore.Surface`, Swarm, and tests | OSS |
| Sample app, Aspire sample host, ServiceDefaults | OSS |
| Blueprint envelope, expanders, Host CRUD/apply | OSS |
| Admin bearer scheme and cluster capabilities | OSS |
| Cloud Server protocol and cluster-side client | OSS |
| Forge server, app, contracts, SQL queue, proxy | Commercial |
| `FabrCore.Surface.Admin` and tests | Commercial |
| `FabrCore.Services.DataIntelligence` | Commercial |
| `FabrCore.Services.GraphRag.Vulcan365` | Commercial |
| Forge Aspire host and ServiceDefaults | Commercial |

## Dependency direction

```text
FabrCore OSS packages
        ↓ NuGet
Surface.Admin / Vulcan365 adapter / Forge
        ↓
Forge App
```

The commercial repository may use conditional sibling `ProjectReference` elements for local
development, but its release and Docker shapes must build with `UseLocalFabrCoreSource=false`.
OSS must never reference the commercial repository.

## Canonical protocol shapes

| Concern | Current shape |
|---|---|
| Blueprint | `FabrCoreBlueprint` |
| Blueprint extension | top-level `"swarm": { "squads": [...] }` |
| Swarm enum | `SurfaceSquadType.Swarm` |
| Swarm messages | `swarm.*` |
| Generated squad handles | `squad-*` |
| Memory admin | `/fabrcoreapi/memory/admin/v1` |
| GraphRAG admin | `/fabrcoreapi/graphrag/admin/v1` |
| Admin authentication | `FabrCoreAdmin` bearer policy |
| Capabilities | cluster capability endpoint + heartbeat map |
| Connect poll | `GET /fabrcore-cloud/v2/connect` |
| Connect result | `POST /fabrcore-cloud/v2/connect/{id}/response` |
| Forge user proxy | `/forgeapi/v1/clusters/{clusterId}/proxy/{**path}` |

## Forbidden regressions

- Duplicate Memory, GraphRAG, Surface, sample, or Contracts projects in the commercial repo.
- OSS references to Forge, Surface.Admin, DataIntelligence, or Vulcan365 services.
- Contract link-compiles or service-package type forwarders.
- Interactive creation wizards in base OSS Surface.
- Inbound cluster administration requirements.
- Forwarding caller `Authorization`, `Host`, or `Content-Length` through the connect channel.
- Silent non-loopback `FabrCore:HostUrl`, alternate remote-administration target URLs, HTTPS loopback assumptions, or shared Forge/local admin keys.
- Real credentials, tracked cloud caches, or enabled Forge defaults in samples.
