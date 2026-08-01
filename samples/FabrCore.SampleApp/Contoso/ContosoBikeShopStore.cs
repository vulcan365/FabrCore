namespace FabrCore.SampleApp.Contoso;

/// <summary>
/// Shared in-memory fake company data for the Contoso Bike Shop Swarm demo.
/// Seeded once at startup; every mutation is really applied so multi-step demos
/// (add a customer, then report on it) observe consistent state across agents.
/// A few seeded customers intentionally share emails with employees so
/// cross-domain demos ("which customers are employees?") have real hits.
/// </summary>
public sealed class ContosoBikeShopStore
{
    private readonly object gate = new();
    private readonly List<ContosoCustomer> customers = [];
    private readonly List<ContosoEmployee> employees = [];
    private readonly List<ContosoCampaign> campaigns = [];
    private int nextCustomerNumber = 9013;
    private int nextEmployeeNumber = 109;
    private int nextCampaignNumber = 307;

    public ContosoBikeShopStore()
    {
        SeedEmployees();
        SeedCustomers();
        SeedCampaigns();
    }

    public IReadOnlyList<ContosoCustomer> SearchCustomers(string? search = null, string? segment = null, string? status = null, int limit = 25)
    {
        lock (gate)
        {
            return customers
                .Where(customer => Matches(search, customer.Id, customer.Name, customer.Email, customer.Segment, customer.Status, customer.Notes)
                    && MatchesExact(segment, customer.Segment)
                    && MatchesExact(status, customer.Status))
                .OrderBy(customer => customer.Id)
                .Take(Math.Clamp(limit, 1, 100))
                .Select(customer => customer.Clone())
                .ToList();
        }
    }

    public ContosoCustomer? GetCustomer(string customerId)
    {
        lock (gate)
        {
            return FindCustomer(customerId)?.Clone();
        }
    }

    public ContosoCustomer AddCustomer(string name, string email, string segment, string? status = null, string? notes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        lock (gate)
        {
            var customer = new ContosoCustomer
            {
                Id = $"CUS-{nextCustomerNumber++}",
                Name = name.Trim(),
                Email = email.Trim(),
                Segment = BlankTo(segment, "Individual"),
                Status = BlankTo(status, "Prospect"),
                TotalSpendUsd = 0,
                Notes = notes?.Trim(),
                JoinedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            customers.Add(customer);
            return customer.Clone();
        }
    }

    public ContosoCustomer UpdateCustomer(string customerId, string? status = null, string? segment = null, string? notes = null, decimal? addPurchaseUsd = null)
    {
        lock (gate)
        {
            var customer = FindCustomer(customerId)
                ?? throw new InvalidOperationException($"Customer '{customerId}' was not found.");

            if (!string.IsNullOrWhiteSpace(status))
            {
                customer.Status = status.Trim();
            }

            if (!string.IsNullOrWhiteSpace(segment))
            {
                customer.Segment = segment.Trim();
            }

            if (!string.IsNullOrWhiteSpace(notes))
            {
                customer.Notes = notes.Trim();
            }

            if (addPurchaseUsd is > 0)
            {
                customer.TotalSpendUsd += addPurchaseUsd.Value;
            }

            customer.UpdatedUtc = DateTime.UtcNow;
            return customer.Clone();
        }
    }

    public IReadOnlyList<ContosoEmployee> SearchEmployees(string? search = null, string? department = null, string? status = null, int limit = 25)
    {
        lock (gate)
        {
            return employees
                .Where(employee => Matches(search, employee.Id, employee.Name, employee.Email, employee.Role, employee.Department, employee.Status)
                    && MatchesExact(department, employee.Department)
                    && MatchesExact(status, employee.Status))
                .OrderBy(employee => employee.Id)
                .Take(Math.Clamp(limit, 1, 100))
                .Select(employee => employee.Clone())
                .ToList();
        }
    }

    public ContosoEmployee? GetEmployee(string employeeId)
    {
        lock (gate)
        {
            return FindEmployee(employeeId)?.Clone();
        }
    }

    public ContosoEmployee AddEmployee(string name, string email, string role, string department, int ptoBalanceDays = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        lock (gate)
        {
            var employee = new ContosoEmployee
            {
                Id = $"EMP-{nextEmployeeNumber++}",
                Name = name.Trim(),
                Email = email.Trim(),
                Role = BlankTo(role, "Sales Associate"),
                Department = BlankTo(department, "Sales Floor"),
                Status = "Active",
                PtoBalanceDays = Math.Clamp(ptoBalanceDays, 0, 40),
                HiredUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            employees.Add(employee);
            return employee.Clone();
        }
    }

    public ContosoEmployee UpdateEmployee(string employeeId, string? status = null, string? role = null, string? department = null, int? ptoBalanceDays = null)
    {
        lock (gate)
        {
            var employee = FindEmployee(employeeId)
                ?? throw new InvalidOperationException($"Employee '{employeeId}' was not found.");

            if (!string.IsNullOrWhiteSpace(status))
            {
                employee.Status = status.Trim();
            }

            if (!string.IsNullOrWhiteSpace(role))
            {
                employee.Role = role.Trim();
            }

            if (!string.IsNullOrWhiteSpace(department))
            {
                employee.Department = department.Trim();
            }

            if (ptoBalanceDays is not null)
            {
                employee.PtoBalanceDays = Math.Clamp(ptoBalanceDays.Value, 0, 40);
            }

            employee.UpdatedUtc = DateTime.UtcNow;
            return employee.Clone();
        }
    }

    public IReadOnlyList<ContosoCampaign> SearchCampaigns(string? search = null, string? status = null, int limit = 25)
    {
        lock (gate)
        {
            return campaigns
                .Where(campaign => Matches(search, campaign.Id, campaign.Name, campaign.Channel, campaign.TargetSegment, campaign.Status, campaign.Notes)
                    && MatchesExact(status, campaign.Status))
                .OrderBy(campaign => campaign.Id)
                .Take(Math.Clamp(limit, 1, 100))
                .Select(campaign => campaign.Clone())
                .ToList();
        }
    }

    public ContosoCampaign? GetCampaign(string campaignId)
    {
        lock (gate)
        {
            return FindCampaign(campaignId)?.Clone();
        }
    }

    public ContosoCampaign CreateCampaign(string name, string channel, string targetSegment, decimal budgetUsd = 0, string? notes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (gate)
        {
            var campaign = new ContosoCampaign
            {
                Id = $"CAM-{nextCampaignNumber++}",
                Name = name.Trim(),
                Channel = BlankTo(channel, "Email"),
                TargetSegment = BlankTo(targetSegment, "Individual"),
                Status = "Draft",
                BudgetUsd = Math.Max(0, budgetUsd),
                LeadsGenerated = 0,
                Notes = notes?.Trim(),
                UpdatedUtc = DateTime.UtcNow
            };
            campaigns.Add(campaign);
            return campaign.Clone();
        }
    }

    public ContosoCampaign UpdateCampaign(string campaignId, string? status = null, string? notes = null, decimal? budgetUsd = null, int? addLeads = null)
    {
        lock (gate)
        {
            var campaign = FindCampaign(campaignId)
                ?? throw new InvalidOperationException($"Campaign '{campaignId}' was not found.");

            if (!string.IsNullOrWhiteSpace(status))
            {
                campaign.Status = status.Trim();
            }

            if (!string.IsNullOrWhiteSpace(notes))
            {
                campaign.Notes = notes.Trim();
            }

            if (budgetUsd is >= 0)
            {
                campaign.BudgetUsd = budgetUsd.Value;
            }

            if (addLeads is > 0)
            {
                campaign.LeadsGenerated += addLeads.Value;
            }

            campaign.UpdatedUtc = DateTime.UtcNow;
            return campaign.Clone();
        }
    }

    public ContosoCompanySnapshot GetSnapshot()
    {
        lock (gate)
        {
            return new ContosoCompanySnapshot
            {
                CustomerCount = customers.Count,
                CustomersBySegment = CountBy(customers, customer => customer.Segment),
                CustomersByStatus = CountBy(customers, customer => customer.Status),
                EmployeeCount = employees.Count,
                EmployeesByDepartment = CountBy(employees, employee => employee.Department),
                EmployeesByStatus = CountBy(employees, employee => employee.Status),
                CampaignCount = campaigns.Count,
                CampaignsByStatus = CountBy(campaigns, campaign => campaign.Status)
            };
        }
    }

    private ContosoCustomer? FindCustomer(string customerId)
        => customers.FirstOrDefault(customer => string.Equals(customer.Id, customerId?.Trim(), StringComparison.OrdinalIgnoreCase));

    private ContosoEmployee? FindEmployee(string employeeId)
        => employees.FirstOrDefault(employee => string.Equals(employee.Id, employeeId?.Trim(), StringComparison.OrdinalIgnoreCase));

    private ContosoCampaign? FindCampaign(string campaignId)
        => campaigns.FirstOrDefault(campaign => string.Equals(campaign.Id, campaignId?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, int> CountBy<T>(IEnumerable<T> items, Func<T, string> keySelector)
        => items
            .GroupBy(keySelector, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private static bool Matches(string? needle, params string?[] values)
        => string.IsNullOrWhiteSpace(needle)
            || values.Any(value => value?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true);

    private static bool MatchesExact(string? expected, string actual)
        => string.IsNullOrWhiteSpace(expected)
            || string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase);

    private static string BlankTo(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private void SeedEmployees()
    {
        employees.AddRange(
        [
            SeedEmployee("EMP-101", "Marcus Reid", "marcus.reid@contosobikes.com", "Store Manager", "Front Office", "Active", 12, months: 62),
            SeedEmployee("EMP-102", "Priya Sharma", "priya.sharma@contosobikes.com", "Service Lead", "Service", "Active", 9, months: 48),
            SeedEmployee("EMP-103", "Jordan Alvarez", "jordan.alvarez@contosobikes.com", "Sales Associate", "Sales Floor", "Active", 15, months: 26),
            SeedEmployee("EMP-104", "Tina Okafor", "tina.okafor@contosobikes.com", "Marketing Coordinator", "Marketing", "Active", 7, months: 19),
            SeedEmployee("EMP-105", "Sam Whitfield", "sam.whitfield@contosobikes.com", "Warehouse Lead", "Warehouse", "On Leave", 2, months: 41),
            SeedEmployee("EMP-106", "Elena Petrov", "elena.petrov@contosobikes.com", "Bike Mechanic", "Service", "Active", 11, months: 33),
            SeedEmployee("EMP-107", "Dave Kim", "dave.kim@contosobikes.com", "Sales Associate", "Sales Floor", "Active", 4, months: 14),
            SeedEmployee("EMP-108", "Rosa Mendez", "rosa.mendez@contosobikes.com", "Office Administrator", "Front Office", "Active", 13, months: 55)
        ]);
    }

    private void SeedCustomers()
    {
        customers.AddRange(
        [
            SeedCustomer("CUS-9001", "Alice Nguyen", "alice.nguyen@fabrikam.com", "Individual", "Active", 1840, "Commutes daily; interested in winter tires."),
            SeedCustomer("CUS-9002", "Redmond Cycling Club", "rides@redmondcc.org", "Club", "Active", 12450, "Group orders every spring; 40 members."),
            SeedCustomer("CUS-9003", "Priya Sharma", "priya.sharma@contosobikes.com", "Individual", "Active", 620, "Employee purchase program."),
            SeedCustomer("CUS-9004", "Ben Carter", "ben.carter@northwind.com", "Individual", "Prospect", 0, "Asked about gravel bikes at the expo."),
            SeedCustomer("CUS-9005", "Contoso Coffee Fleet", "fleet@contosocoffee.com", "Wholesale", "Active", 28900, "Delivery e-bike fleet; quarterly service contract."),
            SeedCustomer("CUS-9006", "Dave Kim", "dave.kim@contosobikes.com", "Individual", "Active", 310, "Employee purchase program."),
            SeedCustomer("CUS-9007", "Maya Rossi", "maya.rossi@adventure-works.com", "Individual", "Lapsed", 940, "No purchase in 14 months; former regular."),
            SeedCustomer("CUS-9008", "Tailwind Triathlon Team", "team@tailwindtri.org", "Club", "Active", 8730, "Race season starts in June."),
            SeedCustomer("CUS-9009", "Omar Haddad", "omar.haddad@proseware.com", "Individual", "Active", 2210, "Mountain bike enthusiast; asks for Elena."),
            SeedCustomer("CUS-9010", "Rosa Mendez", "rosa.mendez@contosobikes.com", "Individual", "Lapsed", 150, "Employee purchase program; inactive lately."),
            SeedCustomer("CUS-9011", "Lucerne Publishing", "office@lucernepublishing.com", "Wholesale", "Prospect", 0, "Evaluating commuter benefit program."),
            SeedCustomer("CUS-9012", "Grace Liu", "grace.liu@fourthcoffee.com", "Individual", "Active", 3480, "Bought two e-bikes; referral source.")
        ]);
    }

    private void SeedCampaigns()
    {
        campaigns.AddRange(
        [
            SeedCampaign("CAM-301", "Spring Tune-Up Special", "Email", "Individual", "Running", 1500, 38, "20% off tune-ups through May."),
            SeedCampaign("CAM-302", "Gravel Group Ride Series", "Event", "Club", "Scheduled", 2500, 12, "Monthly rides with demo bikes."),
            SeedCampaign("CAM-303", "Kids Helmet Safety Push", "Social", "Individual", "Draft", 800, 0, "Partner with local schools."),
            SeedCampaign("CAM-304", "Winter Clearance Blowout", "Email", "All", "Completed", 1200, 87, "Cleared previous-year inventory."),
            SeedCampaign("CAM-305", "Club Membership Drive", "In-Store", "Club", "Running", 600, 21, "Free water bottle for new club referrals."),
            SeedCampaign("CAM-306", "E-Bike Demo Days", "Event", "Individual", "Draft", 3200, 0, "Weekend test rides; needs staffing plan.")
        ]);
    }

    private static ContosoEmployee SeedEmployee(string id, string name, string email, string role, string department, string status, int ptoDays, int months)
        => new()
        {
            Id = id,
            Name = name,
            Email = email,
            Role = role,
            Department = department,
            Status = status,
            PtoBalanceDays = ptoDays,
            HiredUtc = DateTime.UtcNow.AddMonths(-months),
            UpdatedUtc = DateTime.UtcNow
        };

    private static ContosoCustomer SeedCustomer(string id, string name, string email, string segment, string status, decimal totalSpend, string notes)
        => new()
        {
            Id = id,
            Name = name,
            Email = email,
            Segment = segment,
            Status = status,
            TotalSpendUsd = totalSpend,
            Notes = notes,
            JoinedUtc = DateTime.UtcNow.AddMonths(-18),
            UpdatedUtc = DateTime.UtcNow
        };

    private static ContosoCampaign SeedCampaign(string id, string name, string channel, string targetSegment, string status, decimal budget, int leads, string notes)
        => new()
        {
            Id = id,
            Name = name,
            Channel = channel,
            TargetSegment = targetSegment,
            Status = status,
            BudgetUsd = budget,
            LeadsGenerated = leads,
            Notes = notes,
            UpdatedUtc = DateTime.UtcNow
        };
}

public sealed class ContosoCustomer
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Segment { get; set; } = "Individual";

    public string Status { get; set; } = "Prospect";

    public decimal TotalSpendUsd { get; set; }

    public string? Notes { get; set; }

    public DateTime JoinedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public ContosoCustomer Clone() => (ContosoCustomer)MemberwiseClone();
}

public sealed class ContosoEmployee
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string Department { get; set; } = string.Empty;

    public string Status { get; set; } = "Active";

    public int PtoBalanceDays { get; set; }

    public DateTime HiredUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public ContosoEmployee Clone() => (ContosoEmployee)MemberwiseClone();
}

public sealed class ContosoCampaign
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Channel { get; set; } = "Email";

    public string TargetSegment { get; set; } = "Individual";

    public string Status { get; set; } = "Draft";

    public decimal BudgetUsd { get; set; }

    public int LeadsGenerated { get; set; }

    public string? Notes { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public ContosoCampaign Clone() => (ContosoCampaign)MemberwiseClone();
}

public sealed class ContosoCompanySnapshot
{
    public int CustomerCount { get; init; }

    public Dictionary<string, int> CustomersBySegment { get; init; } = [];

    public Dictionary<string, int> CustomersByStatus { get; init; } = [];

    public int EmployeeCount { get; init; }

    public Dictionary<string, int> EmployeesByDepartment { get; init; } = [];

    public Dictionary<string, int> EmployeesByStatus { get; init; } = [];

    public int CampaignCount { get; init; }

    public Dictionary<string, int> CampaignsByStatus { get; init; } = [];
}
