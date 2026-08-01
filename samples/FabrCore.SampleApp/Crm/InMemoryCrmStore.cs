namespace FabrCore.SampleApp.Crm;

public sealed class InMemoryCrmStore
{
    private readonly object _gate = new();
    private readonly List<Customer> _customers;
    private readonly List<Contact> _contacts;

    public InMemoryCrmStore()
    {
        _customers =
        [
            new()
            {
                Id = "CUS-1001",
                Name = "Northwind Manufacturing",
                Segment = "Manufacturing",
                Status = "Active",
                Owner = "Avery Stone",
                AnnualRevenue = 2450000m,
                Notes = "Expanding into two new regions; prioritize onboarding contacts.",
                UpdatedUtc = DateTime.UtcNow.AddDays(-2)
            },
            new()
            {
                Id = "CUS-1002",
                Name = "Contoso Health",
                Segment = "Healthcare",
                Status = "Prospect",
                Owner = "Maya Chen",
                AnnualRevenue = 780000m,
                Notes = "Interested in automated intake workflows.",
                UpdatedUtc = DateTime.UtcNow.AddDays(-5)
            },
            new()
            {
                Id = "CUS-1003",
                Name = "Fabrikam Logistics",
                Segment = "Logistics",
                Status = "At Risk",
                Owner = "Jordan Lee",
                AnnualRevenue = 1375000m,
                Notes = "Escalated because renewal terms are blocked on integration support.",
                UpdatedUtc = DateTime.UtcNow.AddDays(-1)
            },
            new()
            {
                Id = "CUS-1004",
                Name = "Adventure Works",
                Segment = "Retail",
                Status = "Active",
                Owner = "Avery Stone",
                AnnualRevenue = 3200000m,
                Notes = "Strong expansion candidate after storefront analytics pilot.",
                UpdatedUtc = DateTime.UtcNow.AddDays(-7)
            }
        ];

        _contacts =
        [
            new() { Id = "CON-2001", CustomerId = "CUS-1001", FullName = "Priya Raman", Title = "VP Operations", Email = "priya.raman@northwind.example", Phone = "555-0101", Primary = true },
            new() { Id = "CON-2002", CustomerId = "CUS-1001", FullName = "Elliot Hart", Title = "Plant Systems Lead", Email = "elliot.hart@northwind.example", Phone = "555-0102", Primary = false },
            new() { Id = "CON-2003", CustomerId = "CUS-1002", FullName = "Dr. Lena Ortiz", Title = "Chief Medical Officer", Email = "lena.ortiz@contoso.example", Phone = "555-0201", Primary = true },
            new() { Id = "CON-2004", CustomerId = "CUS-1003", FullName = "Marcus Reed", Title = "Director of Integrations", Email = "marcus.reed@fabrikam.example", Phone = "555-0301", Primary = true },
            new() { Id = "CON-2005", CustomerId = "CUS-1004", FullName = "Naomi Blake", Title = "Digital Commerce Manager", Email = "naomi.blake@adventure.example", Phone = "555-0401", Primary = true }
        ];
    }

    public IReadOnlyList<Customer> SearchCustomers(string? search = null, string? status = null)
    {
        lock (_gate)
        {
            return _customers
                .Where(c => MatchesCustomer(c, search) && Matches(status, c.Status))
                .OrderBy(c => c.Name)
                .Select(Clone)
                .ToList();
        }
    }

    public IReadOnlyList<Contact> SearchContacts(string? search = null, string? customerId = null)
    {
        lock (_gate)
        {
            return _contacts
                .Where(c => Matches(search, c.FullName, c.Title, c.Email) && Matches(customerId, c.CustomerId))
                .OrderBy(c => c.FullName)
                .Select(Clone)
                .ToList();
        }
    }

    public Customer? GetCustomer(string customerId)
    {
        lock (_gate)
        {
            return _customers.FirstOrDefault(c => c.Id.Equals(customerId, StringComparison.OrdinalIgnoreCase)) is { } customer
                ? Clone(customer)
                : null;
        }
    }

    public Contact? GetContact(string contactId)
    {
        lock (_gate)
        {
            return _contacts.FirstOrDefault(c => c.Id.Equals(contactId, StringComparison.OrdinalIgnoreCase)) is { } contact
                ? Clone(contact)
                : null;
        }
    }

    public Customer CreateCustomer(string name, string segment, string owner, string status, decimal annualRevenue, string? notes)
    {
        lock (_gate)
        {
            var customer = new Customer
            {
                Id = NextId("CUS", _customers.Select(c => c.Id)),
                Name = name.Trim(),
                Segment = string.IsNullOrWhiteSpace(segment) ? "General" : segment.Trim(),
                Status = string.IsNullOrWhiteSpace(status) ? "Prospect" : status.Trim(),
                Owner = string.IsNullOrWhiteSpace(owner) ? "Demo Owner" : owner.Trim(),
                AnnualRevenue = annualRevenue,
                Notes = notes?.Trim() ?? "",
                UpdatedUtc = DateTime.UtcNow
            };

            _customers.Add(customer);
            return Clone(customer);
        }
    }

    public Contact CreateContact(string customerId, string fullName, string title, string email, string phone, bool primary)
    {
        lock (_gate)
        {
            if (_customers.All(c => !c.Id.Equals(customerId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Customer '{customerId}' was not found.");

            if (primary)
            {
                foreach (var existing in _contacts.Where(c => c.CustomerId.Equals(customerId, StringComparison.OrdinalIgnoreCase)))
                    existing.Primary = false;
            }

            var contact = new Contact
            {
                Id = NextId("CON", _contacts.Select(c => c.Id)),
                CustomerId = customerId.Trim(),
                FullName = fullName.Trim(),
                Title = title.Trim(),
                Email = email.Trim(),
                Phone = phone.Trim(),
                Primary = primary
            };

            _contacts.Add(contact);
            TouchCustomer(customerId);
            return Clone(contact);
        }
    }

    public Customer UpdateCustomerStatus(string customerId, string status)
    {
        lock (_gate)
        {
            var customer = _customers.FirstOrDefault(c => c.Id.Equals(customerId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Customer '{customerId}' was not found.");

            customer.Status = status.Trim();
            customer.UpdatedUtc = DateTime.UtcNow;
            return Clone(customer);
        }
    }

    private void TouchCustomer(string customerId)
    {
        var customer = _customers.FirstOrDefault(c => c.Id.Equals(customerId, StringComparison.OrdinalIgnoreCase));
        if (customer is not null)
            customer.UpdatedUtc = DateTime.UtcNow;
    }

    private static bool MatchesCustomer(Customer customer, string? search)
        => Matches(search, customer.Id, customer.Name, customer.Segment, customer.Status, customer.Owner, customer.Notes);

    private static bool Matches(string? needle, params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(needle))
            return true;

        return values.Any(v => v?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static string NextId(string prefix, IEnumerable<string> ids)
    {
        var next = ids
            .Select(id => id.Split('-', 2))
            .Where(parts => parts.Length == 2 && parts[0] == prefix && int.TryParse(parts[1], out _))
            .Select(parts => int.Parse(parts[1]))
            .DefaultIfEmpty(0)
            .Max() + 1;

        return $"{prefix}-{next:0000}";
    }

    private static Customer Clone(Customer customer) => customer with { };
    private static Contact Clone(Contact contact) => contact with { };
}
