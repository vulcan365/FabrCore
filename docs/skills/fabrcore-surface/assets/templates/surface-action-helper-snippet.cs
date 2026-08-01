using FabrCore.Surface.Builders;

var action = SurfaceActions.ToAgent(
    title: "View",
    verb: "crm.customer.view",
    targetAgent: "crm-agent",
    payload: new { customerId = customer.Id },
    messageTemplate: "show me the customer view for customer {customerId}");
