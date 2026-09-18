# FabrCore SampleApp

This application hosts FabrCore and the Surface chat UI with in-memory CRM, domain squads,
Contoso Bike Shop, and C# scripting examples. Follow the root [quick start](../../README.md#try-the-sample)
to configure the `default` model and launch the app, then open `http://localhost:5248/surface`.

## Upgrading a local configuration from 1.x

Existing gitignored `fabrcore.json` files are not replaced when you update the repository.
If startup reports **JSON ACL seeds/rules are no longer supported**, check that local file
and application configuration for `FabrCore:Acl:Seed`, `Acl:Seed`, or `FabrCore:Acl:Rules`.

The old sample seeded `demo-user` with the `acl-admin` role. Remove that demo seed from the
local configuration: standalone mode does not enforce cross-principal grants, and the sample
bootstrapper registers the demo principal through the ACL API when SQL mode is enabled.
Preserve model/API-key settings. For custom ACL data, use the
[ACL migration procedure](../../docs/database-modes.md#acl-administration-and-migration)
before removing the old configuration. Rebuild and restart SampleApp after updating the file.

## Try C# scripting

Select **scripting-demo** in Surface's agent list (full handle `demo-user:scripting-demo`).
The demo blueprint provisions this agent at startup alongside the CRM agent.

Example prompts:

- “Use C# to calculate the total revenue for all demo sales and break it down by category.”
- “Create a JSON sales report with the order count, total revenue, and totals by category.”

The complete fictional dataset contains three orders totaling **USD 400**: **250** for Bikes
and **150** for Accessories. The agent reads the dataset with `GetDemoSales`, inspects its
environment with `GetScriptingEnvironment`, and calls `ExecuteCSharp` using that data as JSON
input. The reporting example uses Newtonsoft.Json **13.0.3** and decimal arithmetic.

`SalesScriptingPlugin` derives from `CSharpScriptingPluginBase` and supplies package versions,
imports, limits, and example code. `ScriptingDemoAgent` resolves it through the same configured
plugin registry used by ordinary agents. Each script executes in a fresh process; it cannot
reuse variables or files from a previous execution. The example report returns a
`sales-report.json` artifact to the tool caller. Chat summarizes the report; this sample does
not add artifact hosting or a download-link UI.

The first initialization prepares the worker and requires the .NET 10 SDK and NuGet access
(or cached dependencies). Later calls reuse prepared worker assets. The default environment
cache is under the current user's local application data at `FabrCore/Scripting/environments`.
Chat uses your configured model; the automated test below makes no model calls.

**Run this sample as a trusted local demo.** Workers have the application's OS-user permissions;
process separation and execution limits are not a security sandbox. See the
[scripting guide](../../docs/scripting.md) for isolation, prewarming, and deployment options.

## Test

From the repository root:

```powershell
# Run the SampleApp suite, including the real scripting worker example.
dotnet test samples/FabrCore.SampleApp.Tests/FabrCore.SampleApp.Tests.csproj --configuration Release

# Run only the scripting example tests.
dotnet test samples/FabrCore.SampleApp.Tests/FabrCore.SampleApp.Tests.csproj --configuration Release --filter FullyQualifiedName~ScriptingDemoTests
```

The integration test resolves the plugin selected by the actual demo blueprint, invokes its
data and scripting tools, and checks the worker process, total and category calculations,
report artifact, and execution-directory cleanup. It requires SDK/package access but no
model key, SQL server, or running web application. The ordinary repository offline filter
excludes this `Integration` test; release CI runs it explicitly.
