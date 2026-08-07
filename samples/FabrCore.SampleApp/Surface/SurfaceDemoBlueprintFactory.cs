using FabrCore.Core;
using FabrCore.SampleApp.Contoso;
using FabrCore.Surface.Ai.Orchestration;
using FabrCore.Surface.Ai.Squads;
using FabrCore.Surface.CommandCenter;

namespace FabrCore.SampleApp.Surface;

public static class SurfaceDemoBlueprintFactory
{
    public const string PrincipalHandle = "demo-user";

    public const string SurfaceAgentHandle = "surface";

    public const string CrmAgentHandle = "crm-agent";

    public const string AssistantSquadHandle = $"{PrincipalHandle}:squad-assistant";

    public const string ContosoSquadName = "Contoso Bike Shop";

    public const string ContosoSquadOrchestratorHandle = $"{PrincipalHandle}:squad-contoso-bike-shop";

    public const string BlueprintName = "surface-app-demo";

    public const string BlueprintVersion = "2026.07.05";

    public static SurfaceBlueprintDocument Create()
        => new()
        {
            Name = BlueprintName,
            Version = BlueprintVersion,
            Agents =
            [
                new AgentConfiguration
                {
                    Handle = SurfaceAgentHandle,
                    AgentType = "surface",
                    Models = "default",
                    ForceReconfigure = true,
                    Description = "Built-in Surface rendering agent for the SurfaceApp demo",
                    Args = new Dictionary<string, string>
                    {
                        ["surface:Config"] = "crm-demo"
                    }
                },
                new AgentConfiguration
                {
                    Handle = CrmAgentHandle,
                    AgentType = "crm-demo-agent",
                    Models = "default",
                    ForceReconfigure = true,
                    Description = "SurfaceApp CRM records leaf agent"
                }
            ],
            Squads =
            [
                AssistantSquad(),
                SalesSquad(),
                CrmSquad(),
                InventorySquad(),
                AccountsReceivablesSquad(),
                ContosoBikeShopSquad()
            ]
        };

    private static SurfaceSquadDefinition AssistantSquad()
        => new()
        {
            SquadType = SurfaceSquadType.Orchestrator,
            Name = "Assistant",
            Description = "Top-level assistant that routes work to domain branch squads.",
            OrchestratorModel = "default",
            OrchestratorSystemPrompt = """
                You are Assistant, the top-level SurfaceApp demo orchestrator.
                Route the user's request to exactly one branch squad when the domain is clear.
                Use Sales for pipeline, leads, opportunities, quotes, and territory questions.
                Use CRM for accounts, customers, contacts, relationship status, and CRM UI workflows.
                Use Inventory for stock, fulfillment, warehouse, reorder, and allocation questions.
                Use Accounts Receivables for invoices, collections, payments, credit memos, and aging questions.
                Synthesize the branch reply briefly and name the squad you delegated to.
                """,
            ForceReconfigure = true,
            Agents =
            [
                ExistingSquad("Sales", "squad-sales", "Routes lead, opportunity, quote, and territory work."),
                ExistingSquad("CRM", "squad-crm", "Routes customer, contact, account, and relationship work."),
                ExistingSquad("Inventory", "squad-inventory", "Routes stock, fulfillment, reorder, and warehouse work."),
                ExistingSquad("Accounts Receivables", "squad-accounts-receivables", "Routes invoice, payment, aging, and collection work.")
            ]
        };

    private static SurfaceSquadDefinition SalesSquad()
        => BranchSquad(
            "Sales",
            "Handles fake sales pipeline, lead, quote, and territory analysis.",
            [
                Leaf("Lead Intake", "Sales", "Qualifies inbound leads and routes urgent prospects.",
                    "Capture source, urgency, budget fit, and missing qualification fields; Recommend next-best sales action",
                    "LEAD-2048: Contoso expansion inquiry, high urgency; LEAD-2051: Fabrikam webinar attendee, needs discovery",
                    "Prioritize leads with budget and implementation dates; Ask CRM for account history before quoting",
                    "CRM for account context; Inventory for stock-sensitive quote feasibility"),
                Leaf("Opportunity Coach", "Sales", "Reviews opportunities and suggests close-plan actions.",
                    "Assess stage risk, stakeholder gaps, and deal health; Recommend close-plan checkpoints",
                    "OPP-7781: Northwind renewal at negotiation; OPP-7814: Tailspin pilot awaiting technical validation",
                    "Escalate stalled renewals after two inactive touchpoints; Pair high-value pilots with inventory checks",
                    "Accounts Receivables for credit concerns; CRM for stakeholder history"),
                Leaf("Quote Desk", "Sales", "Builds fake quote guidance and pricing readiness notes.",
                    "Check quote completeness, discount rationale, and approval hints; Summarize quote blockers",
                    "QUOTE-4402: 120-seat expansion pending inventory confirmation; QUOTE-4410: services bundle missing delivery owner",
                    "Do not finalize quotes without fulfillment confidence; Flag discounts above demo threshold",
                    "Inventory for availability; Accounts Receivables for payment terms"),
                Leaf("Territory Analyst", "Sales", "Summarizes fake territory and whitespace signals.",
                    "Compare territory coverage, owner load, and expansion signals; Recommend account routing",
                    "TERR-12: Central region has 3 at-risk renewals; TERR-19: West region has 5 expansion candidates",
                    "Route named accounts to assigned owner; Promote underserved expansion clusters",
                    "CRM for ownership data; Assistant for cross-domain synthesis")
            ]);

    private static SurfaceSquadDefinition CrmSquad()
        => BranchSquad(
            "CRM",
            "Handles fake customer, account, contact, and relationship workflows.",
            [
                new SurfaceSquadAgentDefinition
                {
                    Handle = CrmAgentHandle,
                    Name = "CRM Records",
                    AgentType = "crm-demo-agent",
                    Models = "default",
                    Description = "Existing CRM demo agent that can render customer and contact Surface UI.",
                    Role = SurfaceSquadMemberRole.Executor
                },
                Leaf("Account Profile", "CRM", "Summarizes fake account profiles and ownership notes.",
                    "Identify account status, owner, segment, and open risks; Recommend CRM record cleanup",
                    "CUS-1001: Northwind Manufacturing, active strategic account; CUS-1003: Adventure Works, at-risk renewal",
                    "Use CRM Records for UI rendering; Flag missing owner or segment before routing downstream",
                    "Sales for opportunity context; Accounts Receivables for payment concerns"),
                Leaf("Contact Steward", "CRM", "Reviews fake contact coverage and stakeholder gaps.",
                    "Find primary contacts, missing roles, and stale engagement; Suggest next contact action",
                    "CON-3001: Mina Patel, primary operations sponsor; CON-3018: Jorge Chen, finance reviewer missing phone",
                    "Require one primary contact per active customer; Ask for add-contact workflow when buyer roles are missing",
                    "CRM Records for contact forms; Sales for stakeholder strategy"),
                Leaf("Relationship Health", "CRM", "Produces fake relationship health and retention notes.",
                    "Score relationship risk, recent engagement, and renewal readiness; Recommend retention handoff",
                    "HEALTH-18: Northwind healthy with new expansion signal; HEALTH-22: Adventure Works risk increased after support delay",
                    "Escalate at-risk strategic accounts; Keep health notes separate from invoice status",
                    "Accounts Receivables for payment friction; Sales for renewal strategy")
            ]);

    private static SurfaceSquadDefinition InventorySquad()
        => BranchSquad(
            "Inventory",
            "Handles fake inventory, fulfillment, reorder, and warehouse allocation questions.",
            [
                Leaf("Stock Lookup", "Inventory", "Checks fake stock positions and reservation notes.",
                    "Report available, reserved, and constrained stock; Identify item-level shortages",
                    "SKU-AX12: 84 available, 30 reserved; SKU-BR55: 9 available, reorder pending",
                    "Treat low stock as a quote blocker; Prefer available-to-promise over gross stock",
                    "Sales for quote impact; Fulfillment Planner for delivery timing"),
                Leaf("Fulfillment Planner", "Inventory", "Plans fake fulfillment timing and delivery risk.",
                    "Estimate shipment windows, split shipment needs, and delivery blockers; Summarize fulfillment confidence",
                    "FUL-9002: Northwind expansion can ship in two waves; FUL-9011: Tailspin pilot waiting on SKU-BR55",
                    "Split shipments when constrained SKUs block full delivery; Escalate delivery promises with low confidence",
                    "Warehouse Allocator for warehouse choice; Sales for customer messaging"),
                Leaf("Reorder Analyst", "Inventory", "Reviews fake reorder points and replenishment signals.",
                    "Check reorder thresholds, lead times, and demand spikes; Recommend replenishment actions",
                    "REORDER-77: SKU-BR55 below safety stock; REORDER-81: SKU-CX90 projected to dip next week",
                    "Recommend reorder when projected demand crosses safety stock; Do not promise vendor dates as confirmed",
                    "Sales for demand source; Accounts Receivables for vendor hold signals"),
                Leaf("Warehouse Allocator", "Inventory", "Chooses fake warehouse allocation options.",
                    "Compare warehouse capacity, distance, and pick constraints; Recommend allocation plan",
                    "WH-CENTRAL: 71 percent pick capacity; WH-WEST: closer to Tailspin but constrained on SKU-BR55",
                    "Prefer nearest warehouse only when stock and pick capacity are sufficient; Flag split allocations",
                    "Fulfillment Planner for timing; Assistant for multi-domain tradeoffs")
            ]);

    private static SurfaceSquadDefinition AccountsReceivablesSquad()
        => BranchSquad(
            "Accounts Receivables",
            "Handles fake invoice, payment, collections, credit memo, and aging questions.",
            [
                Leaf("Invoice Analyst", "Accounts Receivables", "Reviews fake invoices and billing status.",
                    "Summarize open invoices, due dates, disputes, and billing blockers; Recommend invoice follow-up",
                    "INV-6201: Northwind current, due in 12 days; INV-6188: Adventure Works 32 days past due",
                    "Separate disputed balances from ordinary aging; Flag invoice blockers before collection action",
                    "CRM for account owner; Sales for renewal sensitivity"),
                Leaf("Collections Coordinator", "Accounts Receivables", "Plans fake collection actions and escalation notes.",
                    "Prioritize collection outreach, promise-to-pay status, and escalation timing; Recommend tone",
                    "COL-144: Adventure Works needs owner-aligned outreach; COL-147: Tailspin promised payment Friday",
                    "Avoid aggressive outreach on strategic renewals without CRM context; Escalate broken promises-to-pay",
                    "CRM for relationship risk; Sales for active opportunities"),
                Leaf("Cash Application", "Accounts Receivables", "Matches fake payments and remittance clues.",
                    "Identify unapplied cash, remittance mismatches, and likely invoice matches; Recommend posting path",
                    "PAY-8820: 18000 unapplied from Contoso; PAY-8824: partial payment likely maps to INV-6201",
                    "Do not close invoices without remittance confidence; Flag partial payments for analyst review",
                    "Invoice Analyst for balance impact; CRM for customer communication"),
                Leaf("Credit Memo Specialist", "Accounts Receivables", "Reviews fake credit memo and adjustment requests.",
                    "Check adjustment reason, approval hints, and invoice impact; Recommend memo disposition",
                    "CM-330: freight adjustment requested for Northwind; CM-336: duplicate service fee dispute",
                    "Require reason and owner before approving credits; Route renewal-sensitive credits through Sales",
                    "Invoice Analyst for affected balance; Sales for commercial approval")
            ]);

    /// <summary>
    /// Task squad demo: ten Contoso Bike Shop specialists across CRM, HR, and
    /// Marketing, all backed by the shared tracked in-memory
    /// <see cref="ContosoBikeShopStore"/>. Built to exercise long-running
    /// multi-step plans (5-10 tasks) and cross-domain routing.
    /// </summary>
    private static SurfaceSquadDefinition ContosoBikeShopSquad()
        => new()
        {
            SquadType = SurfaceSquadType.Task,
            Name = ContosoSquadName,
            Description = "Contoso Bike Shop Task squad demo with CRM, HR, and Marketing specialists over tracked in-memory company data.",
            OrchestratorModel = "default",
            ForceReconfigure = true,
            TaskOptions = new SurfaceTaskSquadOptions
            {
                DelegationTimeoutSeconds = 180,
                MaxLoopIterations = 12,
                PersonaPrompt = """
                    Routing guide: Customer Records / Customer Insights / Retention Specialist own CRM customers (CUS-9xxx);
                    Employee Records / Scheduling and Time Off / Recruiting Coordinator own HR employees (EMP-1xx);
                    Campaign Manager / Audience Planner / Content Writer own marketing campaigns (CAM-3xx).
                    Customer Insights can read both CRM and HR data, so cross-reference work such as
                    "which customers are employees" belongs to Customer Insights once the fetch work has returned.
                    For multi-domain goals, start the independent fetches concurrently, then run analysis that
                    depends on them, then mutations, then summarize. Consult the Bike Shop SME when the request
                    is ambiguous or the right specialist is unclear.
                    """
            },
            OrchestratorSystemPrompt = """
                You run the Contoso Bike Shop squad, a demo company with real tracked in-memory data:
                CRM customers (CUS-9xxx), HR employees (EMP-1xx), and marketing campaigns (CAM-3xx).
                Some customers are also employees through the employee purchase program (matched by email).
                Preserve the user's intent, keep responses concise, and synthesize squad results into one readable answer.
                When work was mutated, list the record IDs that were created or changed.
                """,
            Agents =
            [
                ContosoWorker(
                    "Customer Records",
                    "Owns the Contoso CRM customer directory: lists, lookups, adds, and updates.",
                    "Customer Records Specialist",
                    "Search, fetch, add, and update CRM customers (CUS-9xxx). You are the only agent that should add new customers.",
                    "Confirm whether a customer already exists by email before adding; report the new customer ID after every add.",
                    [ContosoCrmPlugin.Alias]),
                ContosoWorker(
                    "Customer Insights",
                    "Analyzes customers across CRM and HR data, including employee-purchase overlap.",
                    "Customer Insights Analyst",
                    "Answer analytical questions about customers: segments, spend, status mixes, and cross-references against the employee roster (customers and employees match by email).",
                    "Fetch fresh data with tools before analyzing; when asked which customers are employees, compare CRM emails against HR emails and list the matches with both IDs.",
                    [ContosoCrmPlugin.Alias, ContosoHrPlugin.Alias]),
                ContosoWorker(
                    "Retention Specialist",
                    "Watches lapsed and at-risk customers and drafts win-back actions.",
                    "Customer Retention Specialist",
                    "Find lapsed or at-risk customers, recommend win-back steps, and record retention notes on customer records.",
                    "Always ground win-back suggestions in the customer's spend, segment, and notes; update the customer's notes when a retention action is decided.",
                    [ContosoCrmPlugin.Alias]),
                ContosoWorker(
                    "Employee Records",
                    "Owns the Contoso HR employee directory: lists, lookups, hires, and updates.",
                    "HR Records Specialist",
                    "Search, fetch, hire, and update employees (EMP-1xx), including roles and departments. You are the only agent that should hire new employees.",
                    "Check for an existing employee by email before hiring; report the employee ID after every hire or update.",
                    [ContosoHrPlugin.Alias]),
                ContosoWorker(
                    "Scheduling and Time Off",
                    "Tracks PTO balances, leave status, and staffing coverage by department.",
                    "Scheduling and Time Off Coordinator",
                    "Answer PTO balance and leave questions, put employees on leave or back to active, and assess department coverage for events.",
                    "Flag departments where leave would drop coverage below two active people; adjust PTO balances only when a task explicitly asks.",
                    [ContosoHrPlugin.Alias]),
                ContosoWorker(
                    "Recruiting Coordinator",
                    "Plans hiring needs and onboards new hires into the HR directory.",
                    "Recruiting Coordinator",
                    "Identify staffing gaps, propose roles to hire, and onboard approved hires with AddEmployee.",
                    "Recommend a department and role before hiring; new hires start with the default PTO balance unless told otherwise.",
                    [ContosoHrPlugin.Alias]),
                ContosoWorker(
                    "Campaign Manager",
                    "Owns marketing campaigns end to end: create, schedule, run, pause, complete.",
                    "Marketing Campaign Manager",
                    "Create campaigns (CAM-3xx), move them through Draft, Scheduled, Running, Paused, and Completed, and keep budgets and lead counts current.",
                    "New campaigns always start in Draft; report the campaign ID and status after every change.",
                    [ContosoMarketingPlugin.Alias]),
                ContosoWorker(
                    "Audience Planner",
                    "Picks target audiences for campaigns using live CRM segment data.",
                    "Marketing Audience Planner",
                    "Recommend target segments and estimate audience sizes for campaigns using real CRM segment and status counts.",
                    "Base audience sizes on GetCrmSnapshot and customer searches, not guesses; exclude lapsed customers unless the campaign is a win-back.",
                    [ContosoMarketingPlugin.Alias, ContosoCrmPlugin.Alias]),
                ContosoWorker(
                    "Content Writer",
                    "Writes short campaign copy and offer text, stored in campaign notes.",
                    "Marketing Content Writer",
                    "Draft concise campaign copy, subject lines, and offers, then save the copy into the campaign's notes with UpdateCampaign.",
                    "Keep copy under 80 words, match the bike shop's friendly voice, and always persist final copy to the campaign record.",
                    [ContosoMarketingPlugin.Alias]),
                ContosoWorker(
                    "Bike Shop SME",
                    "Veteran shop operations manager consulted on ambiguous or cross-domain requests.",
                    "Contoso Bike Shop Operations SME",
                    "Advise the planner and squad on how bike shop work should flow across CRM, HR, and Marketing, using live data snapshots for context.",
                    "Give short, decisive guidance: name the specialist who should own each piece of work and call out data the plan is missing.",
                    [ContosoCrmPlugin.Alias, ContosoHrPlugin.Alias, ContosoMarketingPlugin.Alias],
                    SurfaceSquadMemberRole.SubjectMatterExpert)
            ]
        };

    private static SurfaceSquadAgentDefinition ContosoWorker(
        string name,
        string description,
        string persona,
        string focus,
        string playbook,
        List<string> plugins,
        SurfaceSquadMemberRole role = SurfaceSquadMemberRole.Executor)
        => new()
        {
            Name = name,
            AgentType = ContosoWorkerAgent.Alias,
            Models = "default",
            Description = description,
            Role = role,
            Plugins = plugins,
            Args = new Dictionary<string, string>
            {
                [ContosoWorkerAgent.PersonaArg] = persona,
                [ContosoWorkerAgent.FocusArg] = focus,
                [ContosoWorkerAgent.PlaybookArg] = playbook
            }
        };

    private static SurfaceSquadDefinition BranchSquad(
        string name,
        string description,
        List<SurfaceSquadAgentDefinition> agents)
        => new()
        {
            SquadType = SurfaceSquadType.Orchestrator,
            Name = name,
            Description = description,
            OrchestratorModel = "default",
            OrchestratorSystemPrompt = $"""
                You are the {name} branch squad orchestrator in the SurfaceApp demo.
                Route the request to the best leaf agent in this branch.
                Prefer a specific specialist over a general answer.
                Return the specialist's facts and note any recommended handoff.
                """,
            ForceReconfigure = true,
            Agents = agents
        };

    private static SurfaceSquadAgentDefinition ExistingSquad(string name, string handle, string description)
        => new()
        {
            Handle = handle,
            Name = name,
            AgentType = SurfaceOrchestrationAgentTypes.SquadOrchestrator,
            Models = "default",
            Description = description,
            Role = SurfaceSquadMemberRole.Executor
        };

    private static SurfaceSquadAgentDefinition Leaf(
        string name,
        string domain,
        string description,
        string responsibilities,
        string records,
        string decisions,
        string handoffs)
        => new()
        {
            Name = name,
            AgentType = SurfaceDemoDomainAgent.Alias,
            Models = "default",
            Description = description,
            Role = SurfaceSquadMemberRole.Executor,
            Plugins = [SurfaceDemoDomainPlugin.Alias],
            Args = new Dictionary<string, string>
            {
                [SurfaceDemoDomainAgent.DomainArg] = domain,
                [SurfaceDemoDomainAgent.ProfileArg] = name,
                [SurfaceDemoDomainAgent.ResponsibilitiesArg] = responsibilities,
                [SurfaceDemoDomainAgent.RecordsArg] = records,
                [SurfaceDemoDomainAgent.DecisionsArg] = decisions,
                [SurfaceDemoDomainAgent.HandoffsArg] = handoffs
            }
        };
}
