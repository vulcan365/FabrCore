var data = JObject.Parse(InputJson);
var total = data["orders"]!.Values<decimal>().Sum();
Console.WriteLine("Calculated in worker " + System.Environment.ProcessId);
return new { Customer = data["customer"]!.ToString(), Total = total };
