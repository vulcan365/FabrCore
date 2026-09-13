using FabrCore.Core;
using FabrCore.Core.Acl;
using FabrCore.Host.Services;
using FabrCore.Services.GraphRag;
using FabrCore.Services.Memory.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace FabrCore.Host.Database;

internal static class FabrCoreDatabaseRegistration
{
    internal static void AddServices(IServiceCollection services, IConfiguration configuration, FabrCoreDatabaseOptions database, Type evaluatorType)
    {
        services.AddControllers(o => o.Conventions.Add(new DatabaseEndpointConvention(database.Enabled)));
        if (!database.Enabled)
        {
            services.AddSingleton<IAclEvaluator, StandaloneAclEvaluator>();
            return;
        }
        services.AddSingleton<SqlAclRepository>();
        services.AddSingleton<OperationalDatabase>();
        services.AddSingleton<SqlAuditProvider>();
        services.AddSingleton<SqlVerifiableExecutionStore>();
        services.AddSingleton<SqlA2ATaskStore>();
        services.TryAddSingleton<FabrCore.Host.A2A.IA2ATaskStore>(sp => sp.GetRequiredService<SqlA2ATaskStore>());
        services.AddHostedService<DatabaseSchemaHostedService>();
        services.AddSingleton<GrainBackedAclEntityStore>();
        services.AddSingleton<IAclEntityStore>(sp => sp.GetRequiredService<GrainBackedAclEntityStore>());
        services.AddSingleton<IAclSnapshotProvider>(sp => sp.GetRequiredService<GrainBackedAclEntityStore>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<GrainBackedAclEntityStore>());
        services.AddSingleton(typeof(IAclEvaluator), evaluatorType);
        services.AddAgentMemoryServices(database.MemoryConnectionStringName ?? database.ConnectionStringName,
            options => configuration.GetSection("FabrCore:Memory").Bind(options));
        services.AddMemoryAdministration();
        services.AddGraphRagServices(database.GraphRagConnectionStringName ?? database.ConnectionStringName,
            configuration["FabrCore:GraphRag:ExtractionModelName"]);
        services.AddGraphRagAdministration();
        services.AddHealthChecks().AddCheck<DatabaseReadinessCheck>("fabrcore-database", tags: ["ready"]);
    }
}

internal sealed class StandaloneAclEvaluator : IAclEvaluator
{
    public AclEnforcementMode Mode => AclEnforcementMode.Disabled;
    public AclDecision Evaluate(in AclSubjectContext subject, AclAction action, string resourceHandle)
        => new(AclOutcome.DisabledBypass, Mode, "Standalone trusted workspace; ACL is not enabled.");
}

internal sealed class DatabaseEndpointConvention(bool enabled) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        if (enabled) return;
        foreach (var controller in application.Controllers.ToArray())
        {
            if (controller.ControllerType.IsDefined(typeof(RequiresFabrCoreDatabaseAttribute), true))
                application.Controllers.Remove(controller);
            else
                foreach (var action in controller.Actions.Where(a => a.ActionMethod.IsDefined(typeof(RequiresFabrCoreDatabaseAttribute), true)).ToArray())
                    controller.Actions.Remove(action);
        }
    }
}
