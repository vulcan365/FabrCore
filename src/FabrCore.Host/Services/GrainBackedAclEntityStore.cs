using FabrCore.Core.Acl;
using FabrCore.Core.Interfaces;
using FabrCore.Core.Streaming;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;

namespace FabrCore.Host.Services
{
    /// <summary>
    /// Per-silo ACL store. Writes route to the single-activation <see cref="IAclRegistryGrain"/>;
    /// reads are served from a cached immutable <see cref="AclSnapshot"/> so the evaluator never
    /// performs I/O on the message hot path.
    /// <para>
    /// The cache refreshes on <c>AclChanged</c> stream notifications (best-effort) and on a TTL
    /// timer (<see cref="FabrCoreAclOptions.CacheTtlSeconds"/>) as the fallback, so cross-silo
    /// staleness after a mutation is bounded by the TTL. Local writes refresh immediately.
    /// As a hosted service it also drives ACL bootstrap once the cluster is ready.
    /// </para>
    /// </summary>
    public sealed class GrainBackedAclEntityStore : IAclEntityStore, IAclSnapshotProvider, IHostedService
    {
        private readonly IClusterClient _clusterClient;
        private readonly FabrCoreAclOptions _options;
        private readonly ILogger<GrainBackedAclEntityStore> _logger;

        private AclSnapshot _current;
        private CancellationTokenSource? _stopping;
        private Task? _refreshLoop;
        private StreamSubscriptionHandle<AclChangedNotification>? _subscription;

        public GrainBackedAclEntityStore(
            IClusterClient clusterClient,
            IOptions<FabrCoreAclOptions> options,
            ILogger<GrainBackedAclEntityStore> logger)
        {
            _clusterClient = clusterClient;
            _options = options.Value;
            _logger = logger;

            // Empty snapshot until the registry loads: same-principal traffic and the System
            // bypass work immediately; cross-principal stays denied until grants are visible.
            _current = BuildSnapshot(new AclSnapshotData());
        }

        /// <inheritdoc />
        public AclSnapshot Current => Volatile.Read(ref _current);

        private IAclRegistryGrain Registry
            => _clusterClient.GetGrain<IAclRegistryGrain>(IAclRegistryGrain.WellKnownKey);

        // ── IHostedService ──

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _stopping = new CancellationTokenSource();
            _refreshLoop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _stopping?.Cancel();
            if (_subscription is not null)
            {
                try
                {
                    await _subscription.UnsubscribeAsync();
                }
                catch
                {
                    // best-effort cleanup during shutdown
                }
            }

            if (_refreshLoop is not null)
            {
                try
                {
                    await _refreshLoop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (TimeoutException)
                {
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        private async Task RunAsync(CancellationToken stopping)
        {
            // The silo may still be forming a cluster when hosted services start — retry until
            // the registry grain is reachable, then bootstrap and load the first snapshot.
            var attempt = 0;
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await Registry.EnsureBootstrappedAsync();
                    await RefreshAsync(stopping);
                    _logger.LogInformation("ACL snapshot loaded (version {Version})", Current.Version);
                    break;
                }
                catch (Exception ex) when (!stopping.IsCancellationRequested)
                {
                    attempt++;
                    var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
                    _logger.LogWarning(ex,
                        "ACL bootstrap attempt {Attempt} failed — retrying in {Delay}s", attempt, delay.TotalSeconds);
                    try
                    {
                        await Task.Delay(delay, stopping);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }

            if (stopping.IsCancellationRequested)
                return;

            await TrySubscribeAsync();

            // TTL fallback: poll the version and refresh when it moved.
            var ttl = TimeSpan.FromSeconds(Math.Max(1, _options.CacheTtlSeconds));
            using var timer = new PeriodicTimer(ttl);
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    if (!await timer.WaitForNextTickAsync(stopping))
                        return;

                    var version = await Registry.GetVersionAsync();
                    if (version != Current.Version)
                        await RefreshAsync(stopping);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ACL snapshot TTL refresh failed — retaining version {Version}", Current.Version);
                }
            }
        }

        private async Task TrySubscribeAsync()
        {
            try
            {
                var streamProvider = _clusterClient.GetStreamProvider(StreamConstants.ProviderName);
                var stream = streamProvider.GetStream<AclChangedNotification>(
                    StreamId.Create(StreamConstants.AclChangedNamespace, IAclRegistryGrain.WellKnownKey));
                _subscription = await stream.SubscribeAsync(async (notification, _) =>
                {
                    if (notification.Version != Current.Version)
                        await RefreshAsync(CancellationToken.None);
                });
                _logger.LogDebug("Subscribed to ACL change notifications");
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex,
                    "ACL change-stream subscription unavailable — falling back to TTL refresh every {Ttl}s",
                    _options.CacheTtlSeconds);
            }
        }

        private async Task RefreshAsync(CancellationToken cancellationToken)
        {
            var data = await Registry.GetSnapshotAsync();
            Volatile.Write(ref _current, BuildSnapshot(data));
            _logger.LogDebug("ACL snapshot refreshed to version {Version}", data.Version);
        }

        private AclSnapshot BuildSnapshot(AclSnapshotData data)
            => new(data, _options.AllPrincipalsGroupId, _options.AllAgentsGroupId);

        // ── IAclEntityStore (writes route to the registry grain, then refresh the local cache) ──

        public Task<AclSnapshotData> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Registry.GetSnapshotAsync();

        public Task<long> GetVersionAsync(CancellationToken cancellationToken = default)
            => Registry.GetVersionAsync();

        public Task<AclPrincipal?> GetPrincipalAsync(string handle, CancellationToken cancellationToken = default)
            => Registry.GetPrincipalAsync(handle);

        public async Task UpsertPrincipalAsync(AclPrincipal principal, CancellationToken cancellationToken = default)
        {
            await Registry.UpsertPrincipalAsync(principal);
            await RefreshAsync(cancellationToken);
        }

        public async Task<bool> DeletePrincipalAsync(string handle, CancellationToken cancellationToken = default)
        {
            var deleted = await Registry.DeletePrincipalAsync(handle);
            if (deleted)
                await RefreshAsync(cancellationToken);
            return deleted;
        }

        public Task<AclRole?> GetRoleAsync(string name, CancellationToken cancellationToken = default)
            => Registry.GetRoleAsync(name);

        public async Task UpsertRoleAsync(AclRole role, CancellationToken cancellationToken = default)
        {
            await Registry.UpsertRoleAsync(role);
            await RefreshAsync(cancellationToken);
        }

        public async Task<bool> DeleteRoleAsync(string name, CancellationToken cancellationToken = default)
        {
            var deleted = await Registry.DeleteRoleAsync(name);
            if (deleted)
                await RefreshAsync(cancellationToken);
            return deleted;
        }

        public Task<AclGroup?> GetGroupAsync(string name, CancellationToken cancellationToken = default)
            => Registry.GetGroupAsync(name);

        public async Task UpsertGroupAsync(AclGroup group, CancellationToken cancellationToken = default)
        {
            await Registry.UpsertGroupAsync(group);
            await RefreshAsync(cancellationToken);
        }

        public async Task<bool> DeleteGroupAsync(string name, CancellationToken cancellationToken = default)
        {
            var deleted = await Registry.DeleteGroupAsync(name);
            if (deleted)
                await RefreshAsync(cancellationToken);
            return deleted;
        }

        public async Task AddGroupMemberAsync(string groupName, GroupMember member, CancellationToken cancellationToken = default)
        {
            await Registry.AddGroupMemberAsync(groupName, member);
            await RefreshAsync(cancellationToken);
        }

        public async Task<bool> RemoveGroupMemberAsync(string groupName, GroupMember member, CancellationToken cancellationToken = default)
        {
            var removed = await Registry.RemoveGroupMemberAsync(groupName, member);
            if (removed)
                await RefreshAsync(cancellationToken);
            return removed;
        }

        public Task<PermissionGrant?> GetGrantAsync(string id, CancellationToken cancellationToken = default)
            => Registry.GetGrantAsync(id);

        public async Task UpsertGrantAsync(PermissionGrant grant, CancellationToken cancellationToken = default)
        {
            await Registry.UpsertGrantAsync(grant);
            await RefreshAsync(cancellationToken);
        }

        public async Task<bool> DeleteGrantAsync(string id, CancellationToken cancellationToken = default)
        {
            var deleted = await Registry.DeleteGrantAsync(id);
            if (deleted)
                await RefreshAsync(cancellationToken);
            return deleted;
        }

        public Task<AclEnforcementMode?> GetEnforcementModeOverrideAsync(CancellationToken cancellationToken = default)
            => Registry.GetEnforcementModeOverrideAsync();

        public async Task SetEnforcementModeOverrideAsync(AclEnforcementMode? mode, CancellationToken cancellationToken = default)
        {
            await Registry.SetEnforcementModeOverrideAsync(mode);
            await RefreshAsync(cancellationToken);
        }
    }
}
