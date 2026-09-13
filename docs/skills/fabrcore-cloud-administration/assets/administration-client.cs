// FabrCore.Sdk 2.0.0; run only in a trusted administration process.
using System.Net.Http.Headers;
using FabrCore.Core.CloudServer;
using FabrCore.Sdk;

// Host root, with trailing slash; a cloud broker can supply an equivalent handler.
using var http = new HttpClient { BaseAddress = new Uri("https://cluster.example/") };
var key = Environment.GetEnvironmentVariable("FABRCORE_ADMIN_API_KEY")
    ?? throw new InvalidOperationException("Supply the administration credential.");
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
// Establish this from your authenticated operator, never from a public request header.
http.DefaultRequestHeaders.Add("X-FabrCore-Admin-Actor", "authenticated-operator-id");
var admin = new FabrCoreAdministrationClient(http);
var capabilities = await admin.GetCapabilitiesAsync();
var principals = await admin.GetPrincipalPageAsync(limit: 100);
// Continue using the first page's revision; restart enumeration after HTTP 412.

// Use explicit targets and select a source thread, or null for state-only diagnosis.
var session = await admin.CreateAdminSessionAsync("tenant-a", "assistant",
    new AdminSessionRequest { SourceThreadId = null });
// Persist this ID in your application before sending; reuse it for transport retries.
var turnId = Guid.NewGuid().ToString("N");
var turn = await admin.SubmitAdminTurnAsync("tenant-a", "assistant", session.Id,
    new AdminTurnRequest { TurnId = turnId, Message = "Which captured tools returned errors? Cite the records and identify missing data." });
// Read GetAdminTurnAsync with the same session/turn IDs until terminal status.
// Stopping client polling does not cancel an accepted turn. Do not silently replay
// incomplete turns or use ordinary SendMessage with Channel = "_admin".
