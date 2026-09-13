# FabrCore 2.0 development skill distribution

These skills describe FabrCore 2.0.0, including cloud management and isolated admin
conversations in the same release. They are instructions for coding assistants;
runtime harness skills are a separate, principal-scoped agent feature.

Copy whole skill folders with their references, scripts and assets. Start with
`fabrcore` for discovery, `fabrcore-releases` for migration, or
`fabrcore-cloud-administration` for third-party cloud server and Insights integrations.
Use `fabrcore-connections` for optional OAuth/app credentials, Entra Agent ID,
authenticated MCP, outbound Copilot/Work IQ agents, and encrypted client handoffs.
Each top-level skill identifies the 2.0.0 target in its metadata. Specialist
references identify incomplete capture, persistence and validation boundaries.

## Build the distribution

From the repository root, run:

```powershell
python scripts/Package-Skills.py
# To refresh the website download from this checkout:
python scripts/Package-Skills.py --output C:/repos/Vulcan365-Corporate-Websites/src/FabrCore.Ai/wwwroot/downloads
```

The first command writes `artifacts/skills/fabrcore-skills-2.0.0.zip`. Its root contains
normalized skill-name folders and a manifest with SHA-256 hashes. Packaging validates
metadata, local reference links, bundled protocol/migration parity and archive integrity.
Run the skill-creator frontmatter validator separately when authoring instructions.
No package publishing or website deployment occurs during packaging.

Keep the cloud skill's administration and transport reference copies synchronized
with the documents under `docs`, adjusting their relative links to bundled assets.
Keep its SQL migration and the release skill's migration identical to the canonical
monitoring migration. Broader design references use public repository links so an
extracted distribution does not require a FabrCore checkout.

The connections skill bundles `docs/connections-and-microsoft-integration.md` as
`fabrcore-connections/references/integration.md`, with its migration link adjusted
to the bundled SQL asset. Packaging checks both copies against their sources.
