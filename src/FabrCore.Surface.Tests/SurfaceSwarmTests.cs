using System.Text;
using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using FabrCore.Surface;
using FabrCore.Surface.Ai.Swarm;
using FabrCore.Surface.CommandCenter;
using FabrCore.Surface.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FabrCore.Surface.Tests;

public sealed class SurfaceSwarmTests
{
    [Fact]
    public async Task CreateSquadAsyncCreatesFourShellsAndMembers()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var service = new SurfaceSquadService();

        var result = await service.CreateSquadAsync(context, "owner1", new SurfaceSwarmSquadDefinition
        {
            Name = "Ops Desk",
            Agents =
            [
                new SurfaceSwarmSquadAgentDefinition
                {
                    Name = "executor",
                    AgentType = "research-agent",
                    Role = SurfaceSwarmSquadMemberRole.Executor
                },
                new SurfaceSwarmSquadAgentDefinition
                {
                    Name = "policy",
                    AgentType = "policy-agent",
                    Role = SurfaceSwarmSquadMemberRole.SubjectMatterExpert
                }
            ]
        });

        Assert.Equal(6, context.CreatedAgentConfigurations.Count);
        Assert.Equal("owner1:squad-ops-desk", result.Squad.OrchestratorHandle);
        Assert.Equal("owner1:squad-ops-desk-planner", result.Squad.PlannerHandle);
        Assert.Equal("owner1:squad-ops-desk-supervisor", result.Squad.SupervisorHandle);
        Assert.Equal("owner1:squad-ops-desk-verifier", result.Squad.VerifierHandle);
        Assert.Equal("owner1:squad-ops-desk-executor", result.Squad.Agents[0].Handle);
        Assert.Equal("owner1:squad-ops-desk-policy", result.Squad.Agents[1].Handle);

        var byHandle = context.CreatedAgentConfigurations.ToDictionary(
            config => config.Handle!,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(SurfaceSwarmAgentTypes.Orchestrator, byHandle["owner1:squad-ops-desk"].AgentType);
        Assert.Equal(SurfaceSwarmAgentTypes.Planner, byHandle["owner1:squad-ops-desk-planner"].AgentType);
        Assert.Equal(SurfaceSwarmAgentTypes.Supervisor, byHandle["owner1:squad-ops-desk-supervisor"].AgentType);
        Assert.Equal(SurfaceSwarmAgentTypes.Verifier, byHandle["owner1:squad-ops-desk-verifier"].AgentType);
        Assert.Equal("research-agent", byHandle["owner1:squad-ops-desk-executor"].AgentType);

        foreach (var shellHandle in new[]
        {
            result.Squad.OrchestratorHandle,
            result.Squad.PlannerHandle,
            result.Squad.SupervisorHandle,
            result.Squad.VerifierHandle
        })
        {
            var config = byHandle[shellHandle];
            Assert.True(config.Args.ContainsKey(SurfaceSwarmArgs.SquadDefinition));
            var squad = SurfaceSquadService.TryReadSquad(config);
            Assert.NotNull(squad);
            Assert.Equal(2, squad!.Agents.Count);
        }
    }

    [Fact]
    public void HandleBuilderProducesCanonicalAliases()
    {
        Assert.Equal("ops-desk", SurfaceSwarmSquadHandleBuilder.ToSlug("  Ops Desk! "));
        Assert.Equal("squad-ops-desk", SurfaceSwarmSquadHandleBuilder.BuildOrchestratorAlias("Ops Desk"));
        Assert.Equal("squad-ops-desk-planner", SurfaceSwarmSquadHandleBuilder.BuildPlannerAlias("Ops Desk"));
        Assert.Equal("squad-ops-desk-supervisor", SurfaceSwarmSquadHandleBuilder.BuildSupervisorAlias("Ops Desk"));
        Assert.Equal("squad-ops-desk-verifier", SurfaceSwarmSquadHandleBuilder.BuildVerifierAlias("Ops Desk"));
        Assert.Equal("squad-ops-desk-researcher", SurfaceSwarmSquadHandleBuilder.BuildMemberAlias("Ops Desk", "Researcher"));
        Assert.Equal("owner1:squad-x", SurfaceSwarmSquadHandleBuilder.Qualify("owner1", "squad-x"));
        Assert.Equal("squad-x", SurfaceSwarmSquadHandleBuilder.Qualify(string.Empty, "squad-x"));
        Assert.Equal("Squad Ops Desk", SurfaceSwarmSquadHandleBuilder.DisplayNameFromHandle("owner1:squad-ops-desk"));
        Assert.StartsWith("squad-", SurfaceSwarmSquadHandleBuilder.BuildOrchestratorAlias("anything"));
    }

    [Fact]
    public void LedgerSerializationRoundTrips()
    {
        var policy = new PolicyLedger
        {
            Policy = new ExecutionPolicy
            {
                NeedsPlan = true,
                RiskLevel = "high",
                MaxConcurrency = 2,
                ApprovalRequired = true,
                VerificationDepth = "strict",
                ReplanThreshold = 2
            },
            Budgets = new SurfaceSwarmBudgets { MaxRounds = 7 },
            Round = 3,
            Replans = 1,
            ConsecutiveStalls = 2,
            StartedAt = DateTimeOffset.Parse("2026-07-05T10:00:00Z"),
            RunId = "run1",
            CallerHandle = "owner1:squad-x",
            IsRunning = true
        };
        var ledger = new TaskLedger
        {
            Goal = "Do the thing",
            Facts = ["fact"],
            Hypotheses = ["guess"],
            Revision = 2,
            Tasks =
            [
                new TaskLedgerEntry
                {
                    Id = "t1",
                    Title = "Task one",
                    Description = "Do part one",
                    DependsOn = [],
                    AcceptanceCriteria = ["output exists"],
                    AssignedAgentName = "executor",
                    AssignedAgentHandle = "owner1:squad-x-executor"
                }
            ]
        };
        var progress = new ProgressLedger
        {
            Entries =
            [
                new ProgressEntry
                {
                    TaskId = "t1",
                    Status = SwarmStepStatus.PendingVerification,
                    Attempts = 1,
                    VerificationAttempts = 1,
                    DispatchedAt = DateTimeOffset.Parse("2026-07-05T10:01:00Z"),
                    VerifierFeedback = "missing citation"
                }
            ]
        };
        var artifacts = new ArtifactLedger
        {
            Entries =
            [
                new ArtifactEntry
                {
                    TaskId = "t1",
                    Attempt = 1,
                    Output = "result",
                    Verdict = new SwarmVerdict { Pass = false, Reasons = ["nope"], RetryGuidance = "add citation" },
                    CreatedAt = DateTimeOffset.Parse("2026-07-05T10:02:00Z")
                }
            ]
        };

        var policyJson = JsonSerializer.Serialize(policy, SurfaceJson.Options);
        var ledgerJson = JsonSerializer.Serialize(ledger, SurfaceJson.Options);
        var progressJson = JsonSerializer.Serialize(progress, SurfaceJson.Options);
        var artifactsJson = JsonSerializer.Serialize(artifacts, SurfaceJson.Options);

        Assert.Contains("pendingVerification", progressJson);

        var policyBack = JsonSerializer.Deserialize<PolicyLedger>(policyJson, SurfaceJson.Options)!;
        var ledgerBack = JsonSerializer.Deserialize<TaskLedger>(ledgerJson, SurfaceJson.Options)!;
        var progressBack = JsonSerializer.Deserialize<ProgressLedger>(progressJson, SurfaceJson.Options)!;
        var artifactsBack = JsonSerializer.Deserialize<ArtifactLedger>(artifactsJson, SurfaceJson.Options)!;

        Assert.Equal("run1", policyBack.RunId);
        Assert.Equal(3, policyBack.Round);
        Assert.True(policyBack.Policy.ApprovalRequired);
        Assert.Equal(7, policyBack.Budgets.MaxRounds);
        Assert.Equal("Do the thing", ledgerBack.Goal);
        Assert.Equal(2, ledgerBack.Revision);
        Assert.Equal("owner1:squad-x-executor", ledgerBack.Tasks[0].AssignedAgentHandle);
        Assert.Equal(SwarmStepStatus.PendingVerification, progressBack.Entries[0].Status);
        Assert.Equal("missing citation", progressBack.Entries[0].VerifierFeedback);
        Assert.False(artifactsBack.Entries[0].Verdict!.Pass);
        Assert.Equal("add citation", artifactsBack.Entries[0].Verdict!.RetryGuidance);
    }

    [Fact]
    public void RuntimeFromConfigurationReadsSwarmArgsAndFallsBack()
    {
        var squad = new SurfaceSwarmSquad
        {
            Name = "Ops Desk",
            Slug = "ops-desk",
            PrincipalHandle = "owner1",
            OrchestratorHandle = "owner1:squad-ops-desk",
            PlannerHandle = "owner1:squad-ops-desk-planner",
            SupervisorHandle = "owner1:squad-ops-desk-supervisor",
            VerifierHandle = "owner1:squad-ops-desk-verifier",
            Agents = [new SurfaceSwarmSquadAgent { Name = "executor", Handle = "owner1:squad-ops-desk-executor", AgentType = "research-agent" }]
        };
        var json = SurfaceSwarmSquadRuntime.Serialize(new SurfaceSwarmSquadRuntime { Squad = squad });
        var config = new AgentConfiguration
        {
            Handle = "owner1:squad-ops-desk-supervisor",
            AgentType = SurfaceSwarmAgentTypes.Supervisor,
            Args = new Dictionary<string, string> { [SurfaceSwarmArgs.SquadDefinition] = json }
        };

        var runtime = SurfaceSwarmSquadRuntime.FromConfiguration(config, "owner1:squad-ops-desk-supervisor");
        Assert.Equal("Ops Desk", runtime.Squad.Name);
        Assert.Equal("owner1:squad-ops-desk-verifier", runtime.Squad.VerifierHandle);
        Assert.NotNull(runtime.FindAgent("executor"));
        Assert.NotNull(runtime.FindAgent("squad-ops-desk-executor"));
        Assert.Null(runtime.FindAgent("missing"));

        var fallback = SurfaceSwarmSquadRuntime.FromConfiguration(
            new AgentConfiguration { Handle = "owner1:squad-solo" },
            "owner1:squad-solo");
        Assert.Equal("owner1", fallback.Squad.PrincipalHandle);
        Assert.Equal("owner1:squad-solo", fallback.Squad.OrchestratorHandle);
        Assert.Equal("owner1:squad-solo-planner", fallback.Squad.PlannerHandle);
        Assert.Equal("owner1:squad-solo-supervisor", fallback.Squad.SupervisorHandle);
        Assert.Equal("owner1:squad-solo-verifier", fallback.Squad.VerifierHandle);
    }

    [Fact]
    public void RouteParserResolvesShellAndMemberMentions()
    {
        var squad = new SurfaceSwarmSquad
        {
            OrchestratorHandle = "owner1:squad-x",
            PlannerHandle = "owner1:squad-x-planner",
            SupervisorHandle = "owner1:squad-x-supervisor",
            VerifierHandle = "owner1:squad-x-verifier",
            Agents = [new SurfaceSwarmSquadAgent { Name = "researcher", Handle = "owner1:squad-x-researcher", AgentType = "research-agent" }]
        };

        var plain = SurfaceSwarmSquadRouteParser.Resolve(squad, "do the thing");
        Assert.True(plain.Success);
        Assert.Equal(squad.OrchestratorHandle, plain.TargetHandle);

        Assert.Equal(squad.OrchestratorHandle, SurfaceSwarmSquadRouteParser.Resolve(squad, "@swarm go").TargetHandle);
        Assert.Equal(squad.PlannerHandle, SurfaceSwarmSquadRouteParser.Resolve(squad, "@planner plan it").TargetHandle);
        Assert.Equal(squad.SupervisorHandle, SurfaceSwarmSquadRouteParser.Resolve(squad, "@supervisor status").TargetHandle);
        Assert.Equal(squad.VerifierHandle, SurfaceSwarmSquadRouteParser.Resolve(squad, "@verifier check").TargetHandle);

        var member = SurfaceSwarmSquadRouteParser.Resolve(squad, "@researcher find things");
        Assert.True(member.Success);
        Assert.Equal("owner1:squad-x-researcher", member.TargetHandle);
        Assert.Equal("find things", member.Message);

        var unknown = SurfaceSwarmSquadRouteParser.Resolve(squad, "@nobody hello");
        Assert.False(unknown.Success);
        Assert.NotNull(unknown.Error);
    }

    [Fact]
    public void DependencyResolverComputesWavesReadinessCyclesAndTimeouts()
    {
        var ledger = new TaskLedger
        {
            Tasks =
            [
                new TaskLedgerEntry { Id = "t1" },
                new TaskLedgerEntry { Id = "t2", DependsOn = ["t1"] },
                new TaskLedgerEntry { Id = "t3", DependsOn = ["t1"] },
                new TaskLedgerEntry { Id = "t4", DependsOn = ["t2", "t3"] }
            ]
        };
        var progress = new ProgressLedger
        {
            Entries = ledger.Tasks.Select(task => new ProgressEntry { TaskId = task.Id }).ToList()
        };
        var budgets = new SurfaceSwarmBudgets { MaxTaskAttempts = 2 };

        var waves = SurfaceSwarmDependencyResolver.GetWaves(ledger, progress);
        Assert.Equal(3, waves.Count);
        Assert.Equal(["t1"], waves[0].Select(task => task.Id).ToArray());
        Assert.Equal(["t2", "t3"], waves[1].Select(task => task.Id).OrderBy(id => id).ToArray());
        Assert.Equal(["t4"], waves[2].Select(task => task.Id).ToArray());

        var ready = SurfaceSwarmDependencyResolver.GetReadyEntries(ledger, progress, budgets);
        Assert.Equal(["t1"], ready.Select(task => task.Id).ToArray());

        progress.FindEntry("t1")!.Status = SwarmStepStatus.Completed;
        ready = SurfaceSwarmDependencyResolver.GetReadyEntries(ledger, progress, budgets);
        Assert.Equal(["t2", "t3"], ready.Select(task => task.Id).OrderBy(id => id).ToArray());

        // Attempt budget exhausted → not ready.
        progress.FindEntry("t2")!.Attempts = 2;
        ready = SurfaceSwarmDependencyResolver.GetReadyEntries(ledger, progress, budgets);
        Assert.Equal(["t3"], ready.Select(task => task.Id).ToArray());

        var cyclic = new TaskLedger
        {
            Tasks =
            [
                new TaskLedgerEntry { Id = "a", DependsOn = ["b"] },
                new TaskLedgerEntry { Id = "b", DependsOn = ["a"] }
            ]
        };
        var (isValid, description) = SurfaceSwarmDependencyResolver.ValidateAcyclic(cyclic);
        Assert.False(isValid);
        Assert.Contains("Cycle", description);

        var missingDep = new TaskLedger
        {
            Tasks = [new TaskLedgerEntry { Id = "a", DependsOn = ["ghost"] }]
        };
        (isValid, description) = SurfaceSwarmDependencyResolver.ValidateAcyclic(missingDep);
        Assert.False(isValid);
        Assert.Contains("ghost", description);

        var now = DateTimeOffset.Parse("2026-07-05T10:30:00Z");
        var timedProgress = new ProgressLedger
        {
            Entries =
            [
                new ProgressEntry { TaskId = "t1", Status = SwarmStepStatus.Dispatched, DispatchedAt = now.AddMinutes(-20) },
                new ProgressEntry { TaskId = "t2", Status = SwarmStepStatus.InProgress, DispatchedAt = now.AddSeconds(-30) },
                new ProgressEntry { TaskId = "t3", Status = SwarmStepStatus.Pending, DispatchedAt = now.AddMinutes(-20) }
            ]
        };
        var timedOut = SurfaceSwarmDependencyResolver.GetTimedOut(timedProgress, now, TimeSpan.FromMinutes(5));
        Assert.Equal(["t1"], timedOut.Select(entry => entry.TaskId).ToArray());
    }

    [Fact]
    public void BudgetGuardEnforcesRoundsWallClockReplansAndStalls()
    {
        var now = DateTimeOffset.Parse("2026-07-05T12:00:00Z");
        var progress = new ProgressLedger
        {
            Entries = [new ProgressEntry { TaskId = "t1", Status = SwarmStepStatus.Pending }]
        };

        var healthy = new PolicyLedger
        {
            Budgets = new SurfaceSwarmBudgets { MaxRounds = 5, MaxWallClockMinutes = 30 },
            Round = 3,
            StartedAt = now.AddMinutes(-5)
        };
        Assert.Equal(SwarmBudgetDecision.Continue, SurfaceSwarmBudgetGuard.Evaluate(healthy, progress, now));

        var tooManyRounds = new PolicyLedger
        {
            Budgets = new SurfaceSwarmBudgets { MaxRounds = 5 },
            Round = 6,
            StartedAt = now.AddMinutes(-1)
        };
        Assert.Equal(SwarmBudgetDecision.BudgetExhausted, SurfaceSwarmBudgetGuard.Evaluate(tooManyRounds, progress, now));

        var wallClockExceeded = new PolicyLedger
        {
            Budgets = new SurfaceSwarmBudgets { MaxWallClockMinutes = 30 },
            Round = 1,
            StartedAt = now.AddMinutes(-31)
        };
        Assert.Equal(SwarmBudgetDecision.BudgetExhausted, SurfaceSwarmBudgetGuard.Evaluate(wallClockExceeded, progress, now));

        var failedOut = new PolicyLedger
        {
            Budgets = new SurfaceSwarmBudgets { MaxReplans = 2 },
            Round = 2,
            Replans = 2,
            StartedAt = now.AddMinutes(-1)
        };
        var failedProgress = new ProgressLedger
        {
            Entries = [new ProgressEntry { TaskId = "t1", Status = SwarmStepStatus.Failed }]
        };
        Assert.Equal(SwarmBudgetDecision.BudgetExhausted, SurfaceSwarmBudgetGuard.Evaluate(failedOut, failedProgress, now));

        // Same replans exhausted but work still pending → keep going.
        var stillWorking = new ProgressLedger
        {
            Entries =
            [
                new ProgressEntry { TaskId = "t1", Status = SwarmStepStatus.Failed },
                new ProgressEntry { TaskId = "t2", Status = SwarmStepStatus.Pending }
            ]
        };
        Assert.Equal(SwarmBudgetDecision.Continue, SurfaceSwarmBudgetGuard.Evaluate(failedOut, stillWorking, now));

        Assert.False(SurfaceSwarmBudgetGuard.ShouldEscalate(new PolicyLedger
        {
            Budgets = new SurfaceSwarmBudgets { MaxConsecutiveStalls = 2 },
            ConsecutiveStalls = 1
        }));
        Assert.True(SurfaceSwarmBudgetGuard.ShouldEscalate(new PolicyLedger
        {
            Budgets = new SurfaceSwarmBudgets { MaxConsecutiveStalls = 2 },
            ConsecutiveStalls = 2
        }));

        Assert.True(SurfaceSwarmBudgetGuard.CanReplan(new PolicyLedger
        {
            Budgets = new SurfaceSwarmBudgets { MaxReplans = 2 },
            Replans = 1
        }));
        Assert.False(SurfaceSwarmBudgetGuard.CanReplan(new PolicyLedger
        {
            Budgets = new SurfaceSwarmBudgets { MaxReplans = 2 },
            Replans = 2
        }));
    }

    [Fact]
    public void TriageResultIsClampedByBudgets()
    {
        var budgets = new SurfaceSwarmBudgets { MaxConcurrencyCeiling = 3 };

        var greedy = SurfaceSwarmBudgetGuard.ClampTriage(new SwarmTriageResult
        {
            Mode = "plan",
            RiskLevel = "low",
            MaxConcurrency = 99,
            VerificationDepth = "none"
        }, budgets);
        Assert.True(greedy.NeedsPlan);
        Assert.Equal(3, greedy.MaxConcurrency);
        Assert.Equal("none", greedy.VerificationDepth);
        Assert.False(greedy.ApprovalRequired);

        var risky = SurfaceSwarmBudgetGuard.ClampTriage(new SwarmTriageResult
        {
            Mode = "plan",
            RiskLevel = "HIGH",
            ApprovalRequired = false,
            MaxConcurrency = 0,
            VerificationDepth = "none"
        }, budgets);
        Assert.True(risky.ApprovalRequired);
        Assert.Equal("strict", risky.VerificationDepth);
        Assert.Equal("high", risky.RiskLevel);
        Assert.Equal(1, risky.MaxConcurrency);

        var direct = SurfaceSwarmBudgetGuard.ClampTriage(new SwarmTriageResult { Mode = "direct" }, budgets);
        Assert.False(direct.NeedsPlan);
    }

    [Fact]
    public void PlanValidationRejectsSmeAssignmentsAndUnknownDependencies()
    {
        var members = new List<SurfaceSwarmSquadAgent>
        {
            new() { Name = "executor", Handle = "owner1:squad-x-executor", AgentType = "research-agent", Role = SurfaceSwarmSquadMemberRole.Executor },
            new() { Name = "policy", Handle = "owner1:squad-x-policy", AgentType = "policy-agent", Role = SurfaceSwarmSquadMemberRole.SubjectMatterExpert }
        };

        var smeAssigned = new SwarmLedgerDraft
        {
            Tasks = [new SwarmTaskDraft { Id = "t1", Title = "Bad", AssignedAgentName = "policy" }]
        };
        var errors = SurfaceSwarmPlanValidation.ValidateDraft(smeAssigned, members);
        Assert.Contains(errors, error => error.Contains("not an Executor-role member"));

        var unknownDep = new SwarmLedgerDraft
        {
            Tasks = [new SwarmTaskDraft { Id = "t1", AssignedAgentName = "executor", DependsOn = ["ghost"] }]
        };
        errors = SurfaceSwarmPlanValidation.ValidateDraft(unknownDep, members);
        Assert.Contains(errors, error => error.Contains("ghost"));

        var empty = new SwarmLedgerDraft();
        errors = SurfaceSwarmPlanValidation.ValidateDraft(empty, members);
        Assert.Contains(errors, error => error.Contains("no tasks"));

        var valid = new SwarmLedgerDraft
        {
            Facts = ["fact"],
            Tasks =
            [
                new SwarmTaskDraft { Id = "t1", Title = "One", AssignedAgentName = "executor", AcceptanceCriteria = ["done"] },
                new SwarmTaskDraft { Id = "t2", Title = "Two", AssignedAgentName = "executor", DependsOn = ["t1"] }
            ]
        };
        errors = SurfaceSwarmPlanValidation.ValidateDraft(valid, members);
        Assert.Empty(errors);

        var ledger = SurfaceSwarmPlanValidation.ToLedger(valid, members, "Do the thing", priorLedger: null);
        Assert.Equal("Do the thing", ledger.Goal);
        Assert.Equal(0, ledger.Revision);
        Assert.Equal("owner1:squad-x-executor", ledger.Tasks[0].AssignedAgentHandle);
        Assert.Equal("executor", ledger.Tasks[0].AssignedAgentName);

        var replanned = SurfaceSwarmPlanValidation.ToLedger(valid, members, "ignored", priorLedger: ledger);
        Assert.Equal("Do the thing", replanned.Goal);
        Assert.Equal(1, replanned.Revision);
    }

    [Fact]
    public void JsonExtractionHandlesFencesGarbageAndVerdicts()
    {
        Assert.Null(SwarmJson.Deserialize<SwarmVerifierVerdict>("no json here"));
        Assert.Null(SwarmJson.Deserialize<SwarmVerifierVerdict>("{ definitely broken json"));

        var fenced = """
            Here is the verdict:
            ```json
            {"pass":false,"reasons":["criterion 2 unmet"],"missingItems":["citation"],"retryGuidance":"add a citation"}
            ```
            """;
        var verdict = SwarmVerifierVerdict.Parse(fenced);
        Assert.NotNull(verdict);
        Assert.False(verdict!.Pass);
        Assert.Equal(["citation"], verdict.MissingItems);
        Assert.Equal("add a citation", verdict.ToVerdict().RetryGuidance);

        var triage = SwarmTriageResult.Parse("""{"mode":"plan","riskLevel":"medium","workBrief":"do it"}""");
        Assert.NotNull(triage);
        Assert.Equal("plan", triage!.Mode);
    }

    [Fact]
    public async Task ConversationBusStampsSwarmArgsAndMirrors()
    {
        var host = new FakeSwarmAgentHost("owner1:squad-x-supervisor")
        {
            Responder = request =>
            {
                var response = request.Response();
                response.Message = "done";
                return response;
            }
        };
        var runtime = new SurfaceSwarmSquadRuntime
        {
            Squad = new SurfaceSwarmSquad
            {
                Name = "Ops Desk",
                Slug = "ops-desk",
                PrincipalHandle = "owner1",
                OrchestratorHandle = "owner1:squad-ops-desk"
            }
        };
        var bus = new SurfaceSwarmSquadConversationBus(host, runtime);

        var response = await bus.SendAndReceiveAsync(new AgentMessage
        {
            FromHandle = "owner1:squad-x-supervisor",
            ToHandle = "owner1:squad-ops-desk-executor",
            MessageType = SurfaceSwarmMessageTypes.TaskDispatch,
            Kind = MessageKind.Request,
            Message = "do work"
        });

        Assert.Equal("done", response.Message);
        Assert.Equal("owner1:squad-ops-desk", response.Args![SurfaceSwarmArgs.SquadHandle]);

        // Request mirror + response mirror.
        Assert.Equal(2, host.SentMessages.Count);
        foreach (var mirror in host.SentMessages)
        {
            Assert.Equal("owner1", mirror.ToHandle);
            Assert.Equal("true", mirror.Args![SurfaceSwarmArgs.Mirror]);
            Assert.Equal("owner1:squad-ops-desk", mirror.Channel);

        }

        Assert.Equal("owner1:squad-ops-desk-executor", host.SentMessages[0].Args![SurfaceSwarmArgs.OriginalToHandle]);
        Assert.Equal("owner1:squad-x-supervisor", host.SentMessages[0].Args![SurfaceSwarmArgs.OriginalFromHandle]);
    }

    [Fact]
    public async Task ConversationBusForwardsMemberCardsToPrincipal()
    {
        var cardData = Encoding.UTF8.GetBytes("""{"id":"env1"}""");
        var host = new FakeSwarmAgentHost("owner1:squad-ops-desk")
        {
            Responder = request =>
            {
                var response = request.Response();
                response.MessageType = FabrCore.Surface.Contracts.SurfaceMessageTypes.UiRender;
                response.DataType = FabrCore.Surface.Contracts.SurfaceMessageTypes.DataType;
                response.Data = cardData;
                response.Message = "I rendered the customer list.";
                return response;
            }
        };
        var runtime = new SurfaceSwarmSquadRuntime
        {
            Squad = new SurfaceSwarmSquad
            {
                Name = "Ops Desk",
                Slug = "ops-desk",
                PrincipalHandle = "owner1",
                OrchestratorHandle = "owner1:squad-ops-desk"
            }
        };
        var bus = new SurfaceSwarmSquadConversationBus(host, runtime);

        var response = await bus.SendAndReceiveAsync(new AgentMessage
        {
            FromHandle = "owner1:squad-ops-desk",
            ToHandle = "owner1:crm-agent",
            MessageType = SurfaceSwarmMessageTypes.TaskDispatch,
            Kind = MessageKind.Request,
            Message = "get customers"
        });

        Assert.True(SurfaceSwarmSquadConversationBus.IsAdaptiveCardRender(response));

        // Request mirror + response mirror + forwarded card.
        Assert.Equal(3, host.SentMessages.Count);

        var responseMirror = host.SentMessages[1];
        Assert.Equal("true", responseMirror.Args![SurfaceSquadArgs.Mirror]);
        Assert.Equal(FabrCore.Surface.Contracts.SurfaceMessageTypes.UiRender, responseMirror.MessageType);

        var card = host.SentMessages[2];
        Assert.Equal("owner1", card.ToHandle);
        Assert.Equal("owner1:crm-agent", card.FromHandle);
        Assert.Equal(FabrCore.Surface.Contracts.SurfaceMessageTypes.UiRender, card.MessageType);
        Assert.Equal(FabrCore.Surface.Contracts.SurfaceMessageTypes.DataType, card.DataType);
        Assert.Equal(cardData, card.Data);
        Assert.Equal("owner1:squad-ops-desk", card.Channel);
        Assert.Equal("owner1:squad-ops-desk", card.Args![SurfaceSquadArgs.SquadHandle]);
        Assert.False(card.Args.ContainsKey(SurfaceSquadArgs.Mirror));
        Assert.False(card.Args.ContainsKey(SurfaceSwarmArgs.Mirror));
        Assert.False(card.Args.ContainsKey(SurfaceSquadArgs.OriginalFromHandle));
    }

    [Fact]
    public void ClassifierBucketsSquadStampedCardsUnderSquadTimeline()
    {
        var message = new AgentMessage
        {
            FromHandle = "owner1:crm-agent",
            ToHandle = "owner1",
            MessageType = FabrCore.Surface.Contracts.SurfaceMessageTypes.UiRender,
            DataType = FabrCore.Surface.Contracts.SurfaceMessageTypes.DataType,
            Data = Encoding.UTF8.GetBytes("""{"id":"env1"}"""),
            Kind = MessageKind.Response,
            Args = new Dictionary<string, string>
            {
                [SurfaceSquadArgs.SquadHandle] = "owner1:squad-ops-desk"
            }
        };

        var item = SurfaceMessageClassifier.Classify(message);
        Assert.Equal(SurfaceTimelineItemKind.AdaptiveCard, item.Kind);
        Assert.Equal("owner1:squad-ops-desk", item.AgentHandle);
        Assert.Equal("owner1:crm-agent", item.Author);

        // Without squad args the card still buckets under its source agent.
        message.Args.Remove(SurfaceSquadArgs.SquadHandle);
        item = SurfaceMessageClassifier.Classify(message);
        Assert.Equal("owner1:crm-agent", item.AgentHandle);
    }

    [Fact]
    public async Task SmeConsultantSkipsDeadAndUnknownSmes()
    {
        var host = new FakeSwarmAgentHost("owner1:squad-x-planner")
        {
            Responder = request =>
            {
                if (request.ToHandle == "owner1:squad-x-dead")
                {
                    throw new InvalidOperationException("agent unreachable");
                }

                var response = request.Response();
                if (request.ToHandle == "owner1:squad-x-shrug")
                {
                    response.Message = "unknown";
                    return response;
                }

                response.Message = "Use tolerance class B.";
                response.State ??= new Dictionary<string, string>();
                response.State["sme-status"] = "answered";
                return response;
            }
        };
        var runtime = new SurfaceSwarmSquadRuntime
        {
            Squad = new SurfaceSwarmSquad
            {
                Name = "Ops Desk",
                Slug = "ops-desk",
                PrincipalHandle = "owner1",
                OrchestratorHandle = "owner1:squad-ops-desk",
                Agents =
                [
                    new SurfaceSwarmSquadAgent { Name = "dead", Handle = "owner1:squad-x-dead", AgentType = "sme", Role = SurfaceSwarmSquadMemberRole.SubjectMatterExpert },
                    new SurfaceSwarmSquadAgent { Name = "shrug", Handle = "owner1:squad-x-shrug", AgentType = "sme", Role = SurfaceSwarmSquadMemberRole.SubjectMatterExpert },
                    new SurfaceSwarmSquadAgent { Name = "helpful", Handle = "owner1:squad-x-helpful", AgentType = "sme", Role = SurfaceSwarmSquadMemberRole.SubjectMatterExpert },
                    new SurfaceSwarmSquadAgent { Name = "worker", Handle = "owner1:squad-x-worker", AgentType = "exec", Role = SurfaceSwarmSquadMemberRole.Executor }
                ]
            }
        };
        var bus = new SurfaceSwarmSquadConversationBus(host, runtime);
        var consultant = new SurfaceSwarmSmeConsultant(
            bus,
            runtime,
            "owner1:squad-x-planner",
            TimeSpan.FromSeconds(5),
            NullLogger.Instance);

        Assert.Equal(3, consultant.Smes.Count);

        var all = await consultant.ConsultAllAsync("What tolerance class?");
        Assert.Single(all);
        Assert.Equal("helpful", all[0].SmeName);
        Assert.Equal("Use tolerance class B.", all[0].Answer);

        var first = await consultant.ConsultAsync("What tolerance class?");
        Assert.NotNull(first);
        Assert.Equal("helpful", first!.SmeName);
    }

    [Fact]
    public async Task AddAndRemoveAgentUpdateAllFourShellHandles()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var service = new SurfaceSquadService();

        var created = await service.CreateSquadAsync(context, "owner1", new SurfaceSwarmSquadDefinition
        {
            Name = "Ops Desk",
            Agents = [new SurfaceSwarmSquadAgentDefinition { Name = "executor", AgentType = "research-agent" }]
        });
        context.CreatedAgentConfigurations.Clear();

        var updated = await service.AddExistingAgentAsync(context, created.Squad, new SurfaceSwarmSquadAgent
        {
            Name = "helper",
            Handle = "owner1:existing-helper",
            AgentType = "helper-agent",
            Role = SurfaceSwarmSquadMemberRole.Helper
        });

        Assert.Equal(2, updated.Agents.Count);
        Assert.Equal(4, context.CreatedAgentConfigurations.Count);
        var updatedHandles = context.CreatedAgentConfigurations
            .Select(config => config.Handle)
            .OrderBy(handle => handle, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "owner1:squad-ops-desk",
                "owner1:squad-ops-desk-planner",
                "owner1:squad-ops-desk-supervisor",
                "owner1:squad-ops-desk-verifier"
            },
            updatedHandles);

        foreach (var config in context.CreatedAgentConfigurations)
        {
            var squad = SurfaceSquadService.TryReadSquad(config);
            Assert.NotNull(squad);
            Assert.Contains(squad!.Agents, agent => agent.Name == "helper");
        }

        context.CreatedAgentConfigurations.Clear();
        var removed = await service.RemoveAgentAsync(context, updated, "owner1:existing-helper");
        Assert.Single(removed.Agents);
        Assert.Equal(4, context.CreatedAgentConfigurations.Count);
        foreach (var config in context.CreatedAgentConfigurations)
        {
            var squad = SurfaceSquadService.TryReadSquad(config);
            Assert.NotNull(squad);
            Assert.DoesNotContain(squad!.Agents, agent => agent.Name == "helper");
        }
    }

    [Fact]
    public void InteropMapsDefinitionsSquadsAndRoles()
    {
        var definition = new SurfaceSquadDefinition
        {
            SquadType = SurfaceSquadType.Swarm,
            Name = "Ops Desk",
            Description = "Swarm squad",
            OrchestratorModel = "big",
            PlannerModel = "planner",
            TaskOptions = new SurfaceTaskSquadOptions
            {
                FastModelName = "fast",
                WorkerModelName = "worker",
                MaxTaskAttempts = 3,
                MaxValidationAttempts = 4,
                DelegationTimeoutSeconds = 90
            },
            Agents =
            [
                new SurfaceSquadAgentDefinition { Name = "executor", AgentType = "exec", Role = SurfaceSquadMemberRole.Executor },
                new SurfaceSquadAgentDefinition { Name = "policy", AgentType = "sme", Role = SurfaceSquadMemberRole.SubjectMatterExpert }
            ]
        };

        var swarmDefinition = SurfaceSwarmInterop.ToSwarmDefinition(definition);
        Assert.Equal("Ops Desk", swarmDefinition.Name);
        Assert.Equal("fast", swarmDefinition.FastModel);
        Assert.Equal("big", swarmDefinition.OrchestratorModel);
        Assert.Equal("planner", swarmDefinition.PlannerModel);
        Assert.Equal(3, swarmDefinition.Budgets.MaxTaskAttempts);
        Assert.Equal(4, swarmDefinition.Budgets.MaxValidationAttempts);
        Assert.Equal(90, swarmDefinition.Budgets.PerTaskTimeoutSeconds);
        Assert.Equal(SurfaceSwarmSquadMemberRole.Executor, swarmDefinition.Agents[0].Role);
        Assert.Equal(SurfaceSwarmSquadMemberRole.SubjectMatterExpert, swarmDefinition.Agents[1].Role);

        var swarmSquad = SurfaceSquadService.BuildSquad("owner1", swarmDefinition);
        var surfaceSquad = SurfaceSwarmInterop.ToSurfaceSquad(swarmSquad);
        Assert.Equal(SurfaceSquadType.Swarm, surfaceSquad.SquadType);
        Assert.Equal("owner1:squad-ops-desk", surfaceSquad.OrchestratorHandle);
        Assert.Equal("owner1:squad-ops-desk-planner", surfaceSquad.PlannerHandle);
        Assert.Equal(SurfaceSquadMemberRole.SubjectMatterExpert, surfaceSquad.Agents[1].Role);
        Assert.Equal("fast", surfaceSquad.TaskOptions.FastModelName);

        Assert.Equal("owner1:squad-ops-desk-supervisor", SurfaceSwarmInterop.SupervisorHandle(surfaceSquad));
        Assert.Equal("owner1:squad-ops-desk-verifier", SurfaceSwarmInterop.VerifierHandle(surfaceSquad));

        var roundTripped = SurfaceSwarmInterop.ToSwarmSquad(surfaceSquad);
        Assert.Equal(swarmSquad.OrchestratorHandle, roundTripped.OrchestratorHandle);
        Assert.Equal(swarmSquad.PlannerHandle, roundTripped.PlannerHandle);
        Assert.Equal(swarmSquad.SupervisorHandle, roundTripped.SupervisorHandle);
        Assert.Equal(swarmSquad.VerifierHandle, roundTripped.VerifierHandle);
        Assert.Equal("fast", roundTripped.FastModel);
        Assert.Equal(3, roundTripped.Budgets.MaxTaskAttempts);
        Assert.Equal(SurfaceSwarmSquadMemberRole.SubjectMatterExpert, roundTripped.Agents[1].Role);
    }

    [Fact]
    public async Task BasicSquadServiceRejectsSwarmDefinitions()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var service = new SurfaceBasicSquadService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateSquadAsync(
            context,
            "owner1",
            new SurfaceSquadDefinition
            {
                SquadType = SurfaceSquadType.Swarm,
                Name = "Ops Desk"
            }));
    }

    [Fact]
    public async Task WorkspaceCreateSwarmSquadRoutesToSwarmService()
    {
        var context = new FakeSurfacePrincipalContext("owner1");
        var workspace = new SurfaceWorkspaceService(
            Options.Create(new SurfaceOptions { EnableAgentCreate = true }),
            NullLogger<SurfaceWorkspaceService>.Instance,
            new FakeSurfacePrincipalContextFactory(context));

        await workspace.InitializeAsync(new FabrCore.Surface.Identity.SurfacePrincipalContext("owner1", "Principal One", true, "test"));
        var result = await workspace.CreateSquadAsync(new SurfaceSquadDefinition
        {
            SquadType = SurfaceSquadType.Swarm,
            Name = "Ops Desk",
            OrchestratorModel = "default",
            PlannerModel = "planner",
            Agents =
            [
                new SurfaceSquadAgentDefinition
                {
                    Name = "executor",
                    AgentType = "research-agent",
                    Role = SurfaceSquadMemberRole.Executor
                }
            ]
        });

        Assert.Equal(SurfaceSquadType.Swarm, result.Squad.SquadType);
        Assert.Equal("owner1:squad-ops-desk", result.Squad.OrchestratorHandle);
        Assert.Equal("owner1:squad-ops-desk-planner", result.Squad.PlannerHandle);
        Assert.Single(result.Squad.Agents);

        Assert.Equal(5, context.CreatedAgentConfigurations.Count);
        var types = context.CreatedAgentConfigurations
            .Select(config => config.AgentType)
            .ToList();
        Assert.Contains(SurfaceSwarmAgentTypes.Orchestrator, types);
        Assert.Contains(SurfaceSwarmAgentTypes.Planner, types);
        Assert.Contains(SurfaceSwarmAgentTypes.Supervisor, types);
        Assert.Contains(SurfaceSwarmAgentTypes.Verifier, types);

        Assert.Equal(SurfaceSquadType.Swarm, workspace.SelectedSquad?.SquadType);
        Assert.Equal("owner1:squad-ops-desk", workspace.SelectedSquad?.OrchestratorHandle);
    }

    private sealed class FakeSurfacePrincipalContextFactory : ISurfacePrincipalContextFactory
    {
        private readonly FakeSurfacePrincipalContext context;

        public FakeSurfacePrincipalContextFactory(FakeSurfacePrincipalContext context)
        {
            this.context = context;
        }

        public Task<ISurfacePrincipalContext> CreateAsync(string handle, CancellationToken cancellationToken = default)
            => Task.FromResult<ISurfacePrincipalContext>(context);

        public Task<ISurfacePrincipalContext> GetOrCreateAsync(string handle, CancellationToken cancellationToken = default)
            => Task.FromResult<ISurfacePrincipalContext>(context);

        public Task<bool> ReleaseAsync(string handle)
            => Task.FromResult(true);

        public bool HasContext(string handle)
            => true;
    }

    private sealed class FakeSwarmAgentHost : IFabrCoreAgentHost
    {
        private readonly string handle;

        public FakeSwarmAgentHost(string handle)
        {
            this.handle = handle;
        }

        public Func<AgentMessage, AgentMessage>? Responder { get; set; }

        public List<AgentMessage> SentMessages { get; } = [];

        public List<AgentMessage> RequestMessages { get; } = [];

        public string GetHandle() => handle;

        public Task<AgentMessage> SendAndReceiveMessage(AgentMessage request)
        {
            RequestMessages.Add(request);
            var responder = Responder ?? (message => message.Response());
            return Task.FromResult(responder(request));
        }

        public Task SendMessage(AgentMessage request)
        {
            SentMessages.Add(request);
            return Task.CompletedTask;
        }

        public Task SendEvent(EventMessage request) => Task.CompletedTask;

        public Task<AgentHealthStatus> GetAgentHealth(string? handle = null, HealthDetailLevel detailLevel = HealthDetailLevel.Detailed)
            => Task.FromResult(new AgentHealthStatus
            {
                Handle = handle ?? this.handle,
                State = HealthState.Healthy,
                Timestamp = DateTime.UtcNow,
                IsConfigured = true
            });

        public void RegisterTimer(string timerName, string messageType, string? message, TimeSpan dueTime, TimeSpan period)
        {
        }

        public void UnregisterTimer(string timerName)
        {
        }

        public Task RegisterReminder(string reminderName, string messageType, string? message, TimeSpan dueTime, TimeSpan period)
            => Task.CompletedTask;

        public Task UnregisterReminder(string reminderName) => Task.CompletedTask;

        public FabrCoreChatHistoryProvider GetChatHistoryProvider(string threadId)
            => throw new NotSupportedException();

        public void TrackChatHistoryProvider(FabrCoreChatHistoryProvider provider)
        {
        }

        public Task<List<StoredChatMessage>> GetThreadMessagesAsync(string threadId)
            => Task.FromResult(new List<StoredChatMessage>());

        public Task AddThreadMessagesAsync(string threadId, IEnumerable<StoredChatMessage> messages)
            => Task.CompletedTask;

        public Task ClearThreadAsync(string threadId) => Task.CompletedTask;

        public Task ReplaceThreadMessagesAsync(string threadId, IEnumerable<StoredChatMessage> messages)
            => Task.CompletedTask;

        public Task<Dictionary<string, JsonElement>> GetCustomStateAsync()
            => Task.FromResult(new Dictionary<string, JsonElement>());

        public Task MergeCustomStateAsync(Dictionary<string, JsonElement> changes, IEnumerable<string> deletes)
            => Task.CompletedTask;

        public void SetStatusMessage(string? message)
        {
        }
    }

    private sealed class FakeSurfacePrincipalContext : ISurfacePrincipalContext
    {
        public FakeSurfacePrincipalContext(string handle)
        {
            Handle = handle;
        }

        public string Handle { get; }

        public bool IsDisposed { get; private set; }

        public List<AgentMessage> SentMessages { get; } = [];

        public List<AgentMessage> RequestMessages { get; } = [];

        public List<AgentConfiguration> CreatedAgentConfigurations { get; } = [];

        public Dictionary<string, AgentConfiguration> AgentConfigurations { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<TrackedAgentInfo> TrackedAgents { get; } = [];

        public List<AgentInfo> SharedAgents { get; } = [];

        public event EventHandler<AgentMessage>? AgentMessageReceived;

        public Task<AgentMessage> SendAndReceiveMessage(AgentMessage request)
        {
            RequestMessages.Add(request);
            return Task.FromResult(request.Response());
        }

        public Task SendMessage(AgentMessage request)
        {
            SentMessages.Add(request);
            return Task.CompletedTask;
        }

        public Task SendEvent(EventMessage request) => Task.CompletedTask;

        public Task<AgentHealthStatus> CreateAgent(AgentConfiguration agentConfiguration)
        {
            if (!string.IsNullOrWhiteSpace(agentConfiguration.Handle)
                && !agentConfiguration.Handle.Contains(':', StringComparison.Ordinal))
            {
                agentConfiguration.Handle = $"{Handle}:{agentConfiguration.Handle}";
            }

            CreatedAgentConfigurations.Add(agentConfiguration);
            if (!string.IsNullOrWhiteSpace(agentConfiguration.Handle))
            {
                AgentConfigurations[agentConfiguration.Handle] = agentConfiguration;
            }

            return Task.FromResult(NewHealth(agentConfiguration.Handle ?? Handle, agentConfiguration));
        }

        public Task<AgentHealthStatus> ResetAgent(string handle)
            => Task.FromResult(NewHealth(handle));

        public Task<AgentHealthStatus> GetAgentHealth(string handle, HealthDetailLevel detailLevel = HealthDetailLevel.Basic)
        {
            var configuration = AgentConfigurations.GetValueOrDefault(handle);
            return Task.FromResult(configuration is not null
                ? NewHealth(handle, configuration)
                : new AgentHealthStatus
                {
                    Handle = handle,
                    State = HealthState.NotConfigured,
                    Timestamp = DateTime.UtcNow,
                    IsConfigured = false
                });
        }

        public Task<List<TrackedAgentInfo>> GetTrackedAgents(bool activate = false)
            => Task.FromResult(TrackedAgents);

        public Task<bool> IsAgentTracked(string handle)
            => Task.FromResult(false);

        public Task<List<AgentInfo>> GetAccessibleSharedAgents()
            => Task.FromResult(SharedAgents);

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }

        public void Raise(AgentMessage message)
            => AgentMessageReceived?.Invoke(this, message);

        private AgentHealthStatus NewHealth(string? handle = null, AgentConfiguration? configuration = null)
            => new()
            {
                Handle = handle ?? Handle,
                State = HealthState.Healthy,
                Timestamp = DateTime.UtcNow,
                IsConfigured = true,
                Configuration = configuration
            };
    }
}
