using System.ComponentModel;
using FabrCore.Sdk;

namespace FabrCore.SampleApp.Contoso;

[PluginAlias(Alias)]
[Description("Contoso Bike Shop HR data plugin with tracked in-memory employees.")]
[FabrCoreCapabilities("Lists, searches, hires, and updates Contoso Bike Shop demo employees, including departments, roles, PTO balances, and leave status.")]
[FabrCoreNote("Demo-only plugin. Data is fake but every mutation is really applied and shared across all Contoso demo agents.")]
public sealed class ContosoHrPlugin : ContosoPluginBase
{
    public const string Alias = "contoso-hr-data";

    protected override string TableName => "Employees";

    [Description("List Contoso Bike Shop employees. Optionally filter by search text, department (Sales Floor, Service, Warehouse, Marketing, Front Office), or status (Active, On Leave).")]
    public async Task<string> SearchEmployees(
        [Description("Optional search text matched against id, name, email, role, department, and status.")] string? search = null,
        [Description("Optional exact department filter: Sales Floor, Service, Warehouse, Marketing, or Front Office.")] string? department = null,
        [Description("Optional exact status filter: Active or On Leave.")] string? status = null,
        [Description("Maximum number of employees to return, 1 to 100.")] int limit = 25)
    {
        var results = await RecordDbEffect(
            "SearchEmployees",
            search ?? "all",
            () => Store.SearchEmployees(NullIfBlank(search), NullIfBlank(department), NullIfBlank(status), limit),
            search, department, status, limit.ToString());

        return ToJson(new { Count = results.Count, Employees = results });
    }

    [Description("Get one Contoso Bike Shop employee by ID such as EMP-101.")]
    public async Task<string> GetEmployee(
        [Description("Employee ID such as EMP-101.")] string employeeId)
    {
        var employee = await RecordDbEffect(
            "GetEmployee",
            employeeId,
            () => Store.GetEmployee(employeeId),
            employeeId);

        return employee is null
            ? ToJson(new { Error = $"Employee '{employeeId}' was not found." })
            : ToJson(employee);
    }

    [Description("Hire a new employee into the Contoso Bike Shop. The employee is really stored in memory and visible to every other agent afterward.")]
    public async Task<string> AddEmployee(
        [Description("Employee full name.")] string name,
        [Description("Employee email address.")] string email,
        [Description("Role such as Sales Associate, Bike Mechanic, or Marketing Coordinator.")] string role = "Sales Associate",
        [Description("Department: Sales Floor, Service, Warehouse, Marketing, or Front Office.")] string department = "Sales Floor",
        [Description("Starting PTO balance in days, 0 to 40.")] int ptoBalanceDays = 10)
    {
        var employee = await RecordDbEffect(
            "AddEmployee",
            email,
            () => Store.AddEmployee(name, email, role, department, ptoBalanceDays),
            name, email, role, department);

        return ToJson(new { Message = "Employee hired at Contoso Bike Shop.", Employee = employee });
    }

    [Description("Update an existing Contoso employee's status, role, department, or PTO balance. Use status 'On Leave' to record leave and 'Active' to end it.")]
    public async Task<string> UpdateEmployee(
        [Description("Employee ID such as EMP-101.")] string employeeId,
        [Description("Optional new status: Active or On Leave.")] string? status = null,
        [Description("Optional new role.")] string? role = null,
        [Description("Optional new department: Sales Floor, Service, Warehouse, Marketing, or Front Office.")] string? department = null,
        [Description("Optional new PTO balance in days, 0 to 40.")] int? ptoBalanceDays = null)
    {
        var employee = await RecordDbEffect(
            "UpdateEmployee",
            employeeId,
            () => Store.UpdateEmployee(employeeId, status, role, department, ptoBalanceDays),
            employeeId, status, role, department, ptoBalanceDays?.ToString());

        return ToJson(new { Message = "Employee updated.", Employee = employee });
    }

    [Description("Get a summary snapshot of Contoso HR: employee counts by department and status. Useful for staffing and coverage questions.")]
    public async Task<string> GetHrSnapshot()
    {
        var snapshot = await RecordDbEffect(
            "GetHrSnapshot",
            AgentHandle,
            () => Store.GetSnapshot());

        return ToJson(new
        {
            snapshot.EmployeeCount,
            snapshot.EmployeesByDepartment,
            snapshot.EmployeesByStatus
        });
    }
}
