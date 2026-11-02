using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FabrCore.Connections;
using FabrCore.Core;
using FabrCore.Host.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Orleans;

namespace FabrCore.Services.Connections;

internal interface IConnectionGrain : IGrainWithStringKey
{
    Task<string> Execute(string operation, string body);
}

internal sealed class ConnectionRecord
{
    public ConnectionProfile Profile { get; set; } = new();
    public string Revision { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionVersion { get; set; } = Guid.NewGuid().ToString("N");
    public string? ExternalSubject { get; set; }
    /// <summary>The host's template this came from, while it's unchanged. Null for a profile an administrator saved.</summary>
    public string? TemplateHash { get; set; }
    public TokenMaterial? Grant { get; set; }
    /// <summary>When the provider refused this connection's refresh token, until the person connects it again.</summary>
    public DateTimeOffset? LapsedAt { get; set; }
    public Dictionary<string, TokenMaterial> Tokens { get; set; } = [];
    public Dictionary<string, PendingConnection> Pending { get; set; } = [];
    public Dictionary<string, PendingHandoff> Handoffs { get; set; } = [];
}
internal sealed record PendingHandoff(string PrivateKey, DateTimeOffset ExpiresAt);
internal sealed record PendingConnection(string RedirectUri, string Verifier, string Nonce, DateTimeOffset ExpiresAt);
internal sealed record GrainRequest(string Name, string? Principal = null, string? Agent = null, string? Resource = null,
    string? Revision = null, ConnectionProfile? Profile = null, string? RedirectUri = null, string? State = null,
    string? Code = null, string? Assertion = null, ConnectionHandoffEnvelope? Handoff = null, string? SessionVersion = null, string? LoginHint = null)
{
    public override string ToString() => "[protected connection operation]";
}
internal sealed record AuthorizedResource(ConnectionResource Resource, string SessionVersion);

/// <summary>One serialized owner actor prevents refresh rotation and disconnect races across silos.</summary>
internal sealed class ConnectionGrain(IUserScopedFabrCoreStorageProvider store, FabrCore.Host.Security.IFabrCoreDataProtectionProvider protection,
    OAuthProvider oauth, ConnectionsOptions options, IConnectionHandoffPrincipalValidator handoffValidator) : Grain, IConnectionGrain
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private static readonly TimeSpan AssertionRenewal = TimeSpan.FromMinutes(5);
    private Dictionary<string, ConnectionRecord>? records;
    private string Owner => this.GetPrimaryKeyString();
    private IDataProtector Protector => protection.CreateProtector("FabrCore.Connections.v1", Owner);

    public async Task<string> Execute(string operation, string body)
    {
        try { return await ExecuteCore(operation, body); }
        catch (ConnectionException e) { return Serialize(new { connectionError = e.Code, message = e.Message, notice = e.Notice }); }
        catch (Microsoft.IdentityModel.Tokens.SecurityTokenException) { return Serialize(new { connectionError = "invalid-assertion", message = "Identity validation failed." }); }
        catch (JsonException) { return Serialize(new { connectionError = "invalid-payload", message = "The payload could not be read." }); }
        catch (HttpRequestException) { return Serialize(new { connectionError = "provider-unavailable", message = "The identity provider could not be reached." }); }
        catch (OperationCanceledException) { return Serialize(new { connectionError = "provider-timeout", message = "The identity operation timed out. It was not automatically retried." }); }
    }

    private async Task<string> ExecuteCore(string operation, string body)
    {
        if (!options.Enabled) throw new ConnectionException("feature-disabled", "Connections are disabled.");
        var request = JsonSerializer.Deserialize<GrainRequest>(body, Json)!;
        if (records is null)
        {
            var ciphertext = await store.GetAsync<string>(Owner, "fabrcore.protected-connections", "catalog");
            records = ciphertext is null ? [] : JsonSerializer.Deserialize<Dictionary<string, ConnectionRecord>>(Protector.Unprotect(ciphertext), Json)!;
            if (ApplyTemplates(records, options.Templates, Owner)) await Persist();
        }
        if (operation == "list") return Serialize(records.Values.Select(Status).ToArray());
        if (operation == "assertion-agent")
        {
            if (request.Principal != Owner) throw new ConnectionException("access-denied", "An assertion can only be submitted for its own principal.");
            var changed = false;
            foreach (var candidate in records.Values.Where(r => r.Profile is { Enabled: true, Authentication: ConnectionAuthentication.OnBehalfOf } && r.Profile.AllowedAgents.Contains(request.Agent!, StringComparer.Ordinal)))
                changed |= await ApplyAssertion(candidate, request.Assertion!, onlyWhenExpiring: true);
            if (changed) await Persist();
            return Serialize(changed);
        }
        if (operation == "save")
        {
            ValidateProfile(request.Profile!, options);
            if (options.Templates.ContainsKey(request.Name))
                throw new ConnectionException("invalid-configuration", "This connection comes from the host's templates, so it's changed there.");
            records.TryGetValue(request.Name, out var old);
            if ((old is null && request.Revision != "*") || (old is not null && request.Revision != old.Revision))
                throw new ConnectionException("revision-conflict", "Read the current revision before updating the connection.");
            if (request.Profile!.Name != request.Name) throw new ConnectionException("invalid-configuration", "Connection name mismatch.");
            // Configuration changes invalidate consent cache and outstanding transactions.
            records[request.Name] = new() { Profile = request.Profile };
            await Persist();
            return Serialize(Status(records[request.Name]));
        }
        if (!records.TryGetValue(request.Name, out var record)) throw new ConnectionException("not-found", "Connection not found.");
        if (operation == "profile") return Serialize(new { record.Profile, record.Revision });
        if (operation == "disconnect")
        {
            record.Grant = null; record.LapsedAt = null; record.ExternalSubject = null; record.Tokens.Clear(); record.Pending.Clear(); record.Handoffs.Clear();
            record.SessionVersion = Guid.NewGuid().ToString("N");
            await Persist(); return Serialize(Status(record));
        }
        if (!record.Profile.Enabled) throw new ConnectionException("feature-disabled", "This connection is disabled.");
        var p = record.Profile;
        if (p.Authentication is ConnectionAuthentication.AgentIdApplication or ConnectionAuthentication.AgentIdOnBehalfOf && !options.EntraAgentIdEnabled)
            throw new ConnectionException("feature-disabled", "Entra Agent ID is disabled.");
        if (operation.StartsWith("handoff-", StringComparison.Ordinal))
        {
            if (!options.ClientHandoffEnabled) throw new ConnectionException("feature-disabled", "Encrypted client handoffs are disabled.");
            if (operation == "handoff-create")
            {
                foreach (var expired in record.Handoffs.Where(x => x.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(x => x.Key).ToArray()) record.Handoffs.Remove(expired);
                if (record.Handoffs.Count >= 5) throw new ConnectionException("too-many-transactions", "Too many outstanding handoffs.");
                using var key = RSA.Create(2048);
                var id = Random(); var expires = DateTimeOffset.UtcNow.AddMinutes(5);
                record.Handoffs[id] = new(Convert.ToBase64String(key.ExportPkcs8PrivateKey()), expires);
                await Persist();
                return Serialize(new ConnectionHandoffChallenge(id, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), expires));
            }
            var envelope = request.Handoff ?? throw new ConnectionException("invalid-transaction", "Missing encrypted handoff.");
            if (!record.Handoffs.Remove(envelope.Id, out var handoff) || handoff.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new ConnectionException("invalid-transaction", "Handoff is missing, expired, or consumed.");
            await Persist();
            var payload = DecryptHandoff(envelope, handoff);
            if (await handoffValidator.ValidateAsync(payload.UserProof, CancellationToken.None) != Owner)
                throw new ConnectionException("access-denied", "Handoff proof does not identify the connection owner.");
            if (payload.Operation is not ("begin" or "complete" or "assertion" or "disconnect"))
                throw new ConnectionException("unsupported-operation", "Unsupported user handoff operation.");
            return await ExecuteCore(payload.Operation, Serialize(new GrainRequest(request.Name, RedirectUri: payload.RedirectUri,
                State: payload.State, Code: payload.AuthorizationCode, Assertion: payload.Assertion)));
        }
        if (operation == "begin")
        {
            if (p.Authentication != ConnectionAuthentication.AuthorizationCode)
                throw new ConnectionException("unsupported-flow", "This connection uses application credentials or a client-provided Agent ID user assertion.");
            if (!p.RedirectUris.Contains(request.RedirectUri!, StringComparer.Ordinal))
                throw new ConnectionException("access-denied", "The redirect URI is not registered for this connection.");
            foreach (var key in record.Pending.Where(x => x.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(x => x.Key).ToArray()) record.Pending.Remove(key);
            if (record.Pending.Count >= 5) throw new ConnectionException("too-many-transactions", "Complete or wait for pending authorization transactions to expire.");
            var state = Random(); var verifier = Random(); var nonce = Random(); var expires = DateTimeOffset.UtcNow.AddMinutes(10);
            record.Pending[state] = new(request.RedirectUri!, verifier, nonce, expires);
            var metadata = await oauth.Metadata(p, CancellationToken.None);
            var query = new Dictionary<string, string?> {
                ["client_id"] = p.ClientId, ["response_type"] = "code", ["redirect_uri"] = request.RedirectUri,
                ["scope"] = string.Join(' ', p.SignInScopes), ["state"] = state, ["nonce"] = nonce,
                ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"] = "S256"
            };
            if (p.Provider == "google") { query["access_type"] = "offline"; query["prompt"] = "consent"; }
            else if (!string.IsNullOrWhiteSpace(request.LoginHint)) query["login_hint"] = request.LoginHint;
            await Persist();
            return Serialize(new ConnectionTransaction(state, QueryHelpers.AddQueryString(metadata.AuthorizationEndpoint, query), expires));
        }
        if (operation == "complete")
        {
            if (request.State is null || !record.Pending.Remove(request.State, out var pending) || pending.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new ConnectionException("invalid-transaction", "Authorization transaction is missing, expired, or already consumed.");
            // Consume durably before the external exchange. A lost exchange response requires reconnect.
            await Persist();
            if (string.IsNullOrWhiteSpace(request.Code)) return Serialize(Status(record));
            var token = await oauth.Exchange(p, new() {
                ["grant_type"] = "authorization_code", ["code"] = request.Code, ["redirect_uri"] = pending.RedirectUri, ["code_verifier"] = pending.Verifier
            }, CancellationToken.None);
            if (token.IdToken is null) throw new ConnectionException("invalid-assertion", "OpenID identity confirmation is required.");
            var subject = await oauth.Validate(p, token.IdToken, p.ClientId, pending.Nonce, CancellationToken.None);
            record.ExternalSubject = SignedInAccount(p, subject, Owner);
            token.IdToken = null;
            record.Grant = token; record.LapsedAt = null; record.Tokens.Clear();
            record.SessionVersion = Guid.NewGuid().ToString("N");
            // Initial access token is only cached when the requested scopes unambiguously identify one resource.
            if (p.Resources.Count == 1 && p.Resources.Values.Single().Scopes.All(p.SignInScopes.Contains)) record.Tokens[p.Resources.Keys.Single()] = token;
            await Persist(); return Serialize(Status(record));
        }
        if (operation == "assertion")
        {
            if (p.Authentication is not (ConnectionAuthentication.AgentIdOnBehalfOf or ConnectionAuthentication.OnBehalfOf))
                throw new ConnectionException("unsupported-flow", "User assertions require an Agent ID OBO connection.");
            await ApplyAssertion(record, request.Assertion!, onlyWhenExpiring: false);
            await Persist(); return Serialize(Status(record));
        }
        if (operation == "session")
        {
            Authorize(p, Owner, request.Principal!, request.Agent!);
            return Serialize(record.SessionVersion);
        }
        if (operation == "token" || operation == "resource")
        {
            Authorize(p, Owner, request.Principal!, request.Agent!);
            if (!p.Resources.TryGetValue(request.Resource!, out var resource)) throw new ConnectionException("access-denied", "Resource is not configured.");
            if (request.SessionVersion is not null && request.SessionVersion != record.SessionVersion)
                throw new ConnectionException("connection-changed", "The connection changed. Start a new request using its current binding.");
            if (operation == "resource") return Serialize(new AuthorizedResource(resource, record.SessionVersion));
            if (record.Tokens.TryGetValue(request.Resource!, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return Serialize(cached);
            TokenMaterial token;
            try { token = await oauth.Acquire(p, resource, record.Grant, CancellationToken.None, cached); }
            catch (ConnectionException e) when (e.Code == "interaction-required" && p.Authentication == ConnectionAuthentication.AuthorizationCode && record.Grant is not null)
            {
                // The provider won't refresh it any more. It's no use kept, and the person hears once, from the agent that found out.
                record.Grant = null; record.Tokens.Clear(); record.LapsedAt = DateTimeOffset.UtcNow;
                record.SessionVersion = Guid.NewGuid().ToString("N");
                await Persist();
                throw new ConnectionException(e.Code, e.Message) { Notice = LapseNotice(p.Name, options.ReconnectUrl) };
            }
            token.RefreshToken ??= cached?.RefreshToken;
            record.Tokens[request.Resource!] = token;
            if (p.Authentication == ConnectionAuthentication.AuthorizationCode && record.Grant is not null && token.RefreshToken is not null)
                record.Grant.RefreshToken = token.RefreshToken;
            await Persist(); return Serialize(token);
        }
        throw new ConnectionException("unsupported-operation", "Unsupported connection operation.");
    }

    /// <summary>Takes a person's token as this connection's grant. Returns false when it keeps the one it has.</summary>
    private async Task<bool> ApplyAssertion(ConnectionRecord record, string assertion, bool onlyWhenExpiring)
    {
        var subject = await oauth.ValidateAssertion(record.Profile, assertion, CancellationToken.None);
        var tid = subject.FindFirst("tid")?.Value; var oid = subject.FindFirst("oid")?.Value;
        if (string.IsNullOrEmpty(tid) || string.IsNullOrEmpty(oid) || string.IsNullOrEmpty(subject.FindFirst("scp")?.Value)
            || EntraPrincipalHandle.Create(tid, oid) != Owner)
            throw new ConnectionException("access-denied", "The assertion must represent this connection's authenticated Entra principal.");
        // Replacing the grant clears the tokens made from it, so a chat that sends one each turn mustn't do that each turn.
        if (onlyWhenExpiring && record.ExternalSubject == oid && record.Grant?.ExpiresAt > DateTimeOffset.UtcNow.Add(AssertionRenewal))
            return false;
        var exp = long.Parse(subject.FindFirst("exp")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        record.Grant = new() { AccessToken = assertion, ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(exp) };
        if (record.ExternalSubject != oid) record.SessionVersion = Guid.NewGuid().ToString("N");
        record.ExternalSubject = oid; record.Tokens.Clear();
        return true;
    }

    /// <summary>
    /// Brings a person's connections in step with the host's templates: makes one the first time, replaces one whose template
    /// changed, which ends its sign-in because its permissions may have, and drops one whose template has gone, with its tokens.
    /// A profile an administrator saved under the same name stays theirs. Returns whether anything changed.
    /// </summary>
    internal static bool ApplyTemplates(Dictionary<string, ConnectionRecord> records, IReadOnlyDictionary<string, ConnectionProfile> templates, string owner)
    {
        var changed = false;
        foreach (var (name, template) in templates)
        {
            var hash = TemplateHash(template);
            if (records.TryGetValue(name, out var existing) && (existing.TemplateHash is null || existing.TemplateHash == hash))
                continue;
            var profile = JsonSerializer.Deserialize<ConnectionProfile>(Serialize(template), Json)!;
            profile.Name = name;
            profile.AllowedAgents = template.AllowedAgents.Select(handle => $"{owner}:{handle}").ToList();
            records[name] = new() { Profile = profile, TemplateHash = hash };
            changed = true;
        }
        foreach (var gone in records.Where(r => r.Value.TemplateHash is not null && !templates.ContainsKey(r.Key)).Select(r => r.Key).ToArray())
        {
            records.Remove(gone);
            changed = true;
        }
        return changed;
    }

    private static string TemplateHash(ConnectionProfile template) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(template))));

    /// <summary>What a template has to be, checked when the host starts so a bad one can't wait for someone's first use.</summary>
    internal static void ValidateTemplate(string name, ConnectionProfile template, ConnectionsOptions options)
    {
        template.Name = name;
        ValidateProfile(template, options);
        if (template.Authentication is not (ConnectionAuthentication.AuthorizationCode or ConnectionAuthentication.OnBehalfOf))
            throw new ConnectionException("invalid-configuration", $"Connection template '{name}' acts for one person each, so it must use AuthorizationCode or OnBehalfOf.");
        if (template.AllowedAgents.Count == 0 || template.AllowedAgents.Any(a => string.IsNullOrWhiteSpace(a) || a.Contains(':')))
            throw new ConnectionException("invalid-configuration", $"Connection template '{name}' names its allowed agents by bare handle: each person's own agent of that handle.");
    }

    internal static ConnectionHandoffPayload DecryptHandoff(ConnectionHandoffEnvelope envelope, PendingHandoff handoff)
    {
        try
        {
            if (envelope.Ciphertext.Length > 90 * 1024) throw new CryptographicException();
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(handoff.PrivateKey), out _);
            var key = rsa.Decrypt(Convert.FromBase64String(envelope.WrappedKey), RSAEncryptionPadding.OaepSHA256);
            var ciphertext = Convert.FromBase64String(envelope.Ciphertext); var plaintext = new byte[ciphertext.Length];
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(Convert.FromBase64String(envelope.Nonce), ciphertext, Convert.FromBase64String(envelope.Tag), plaintext, Encoding.UTF8.GetBytes(envelope.Id));
                return JsonSerializer.Deserialize<ConnectionHandoffPayload>(plaintext, Json) ?? throw new CryptographicException();
            }
            finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException or JsonException)
        { throw new ConnectionException("invalid-transaction", "The encrypted handoff could not be validated."); }
    }
    internal static void Authorize(ConnectionProfile p, string owner, string principal, string agent)
    {
        if (!p.AllowedAgents.Contains(agent, StringComparer.Ordinal)) throw new ConnectionException("access-denied", "Agent is not permitted to use this connection.");
        if (p.Authentication is ConnectionAuthentication.AuthorizationCode or ConnectionAuthentication.AgentIdOnBehalfOf or ConnectionAuthentication.OnBehalfOf && owner != principal)
            throw new ConnectionException("access-denied", "Delegated connections cannot be shared across principals.");
    }
    internal static void ValidateProfile(ConnectionProfile p, ConnectionsOptions options)
    {
        if (p is null || string.IsNullOrWhiteSpace(p.Name) || !System.Text.RegularExpressions.Regex.IsMatch(p.Name, "^[a-zA-Z0-9_-]{1,100}$") || string.IsNullOrWhiteSpace(p.ClientId))
            throw new ConnectionException("invalid-configuration", "A connection name and client ID are required.");
        if (p.Resources is null || p.AllowedAgents is null || p.RedirectUris is null || p.SignInScopes is null)
            throw new ConnectionException("invalid-configuration", "Connection collections cannot be null.");
        OAuthProvider.RequireHttps(p.Authority);
        if (p.Provider is not ("microsoft" or "google" or "oidc")) throw new ConnectionException("invalid-configuration", "Unknown provider.");
        if (!Enum.IsDefined(p.Authentication)) throw new ConnectionException("invalid-configuration", "Unknown authentication mode.");
        if (p.Authentication is ConnectionAuthentication.AgentIdApplication or ConnectionAuthentication.AgentIdOnBehalfOf)
        {
            if (!options.EntraAgentIdEnabled || p.Provider != "microsoft" || string.IsNullOrWhiteSpace(p.AgentIdentityClientId))
                throw new ConnectionException("feature-disabled", "Agent ID must be explicitly enabled and bound to an identity.");
            if (p.Authentication == ConnectionAuthentication.AgentIdOnBehalfOf && string.IsNullOrWhiteSpace(p.BlueprintAudience))
                throw new ConnectionException("invalid-configuration", "The blueprint API audience is required.");
        }
        if (p.Authentication != ConnectionAuthentication.AuthorizationCode && string.IsNullOrWhiteSpace(p.CredentialReference))
            throw new ConnectionException("invalid-configuration", "An application credential reference is required.");
        if (p.Authentication == ConnectionAuthentication.AuthorizationCode && !p.SignInScopes.Contains("openid"))
            throw new ConnectionException("invalid-configuration", "Authorization code connections require the openid scope.");
        foreach (var uri in p.RedirectUris) OAuthProvider.RequireHttps(uri);
        if (p.Resources.Count == 0) throw new ConnectionException("invalid-configuration", "At least one resource is required.");
        foreach (var resource in p.Resources.Values)
        {
            OAuthProvider.RequireHttps(resource.BaseUrl);
            if (!resource.BaseUrl.EndsWith('/') || new Uri(resource.BaseUrl).Query.Length > 0 || resource.Scopes is null || resource.Scopes.Count == 0 || resource.Scopes.Any(string.IsNullOrWhiteSpace))
                throw new ConnectionException("invalid-configuration", "Resource URLs must end in / and declare scopes.");
        }
    }
    /// <summary>
    /// Who signed in to finish connecting. With Microsoft, an Entra principal can only connect their own account: someone signed in
    /// as one person mustn't give their agents another's mail. Recorded as the object id, as a person's own token records it.
    /// </summary>
    internal static string SignedInAccount(ConnectionProfile p, System.Security.Claims.ClaimsPrincipal signedIn, string owner)
    {
        if (p.Provider != "microsoft")
            return signedIn.FindFirst("sub")?.Value ?? throw new ConnectionException("invalid-assertion", "Missing external subject.");
        var tid = signedIn.FindFirst("tid")?.Value; var oid = signedIn.FindFirst("oid")?.Value;
        if (string.IsNullOrEmpty(oid)) throw new ConnectionException("invalid-assertion", "Missing external subject.");
        if (owner.StartsWith("entra-", StringComparison.Ordinal) && EntraPrincipalHandle.Create(tid, oid) != owner)
            throw new ConnectionException("access-denied", "Sign in with the same account you're connecting for. Another account was used, so nothing was connected.");
        return oid;
    }

    internal static string LapseNotice(string connection, string? reconnectUrl)
        => $"I can't act for you through '{connection}' any more: your sign-in for it has lapsed. "
           + (reconnectUrl is null ? "Connect it again to let me carry on." : $"Connect it again at {reconnectUrl} to let me carry on.");

    private static ConnectionStatus Status(ConnectionRecord r) => new(r.Profile.Name, r.Profile.Enabled,
        !r.Profile.Enabled ? "disabled" : r.Profile.Authentication is ConnectionAuthentication.ClientCredentials or ConnectionAuthentication.AgentIdApplication
        ? "configured" : r.LapsedAt is not null ? "interaction-required" : r.Grant is null ? "connection-required" : r.Grant.RefreshToken is null && r.Grant.ExpiresAt <= DateTimeOffset.UtcNow
            && !r.Tokens.Values.Any(t => t.RefreshToken is not null || t.ExpiresAt > DateTimeOffset.UtcNow) ? "interaction-required" : "connected", r.Revision, r.ExternalSubject);
    private async Task Persist()
    {
        try { await store.UpsertAsync(Owner, "fabrcore.protected-connections", "catalog", Protector.Protect(Serialize(records))); }
        catch { records = null; throw; }
    }
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static string Random() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
}
