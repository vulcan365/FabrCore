using System.Text.Json;
using FabrCore.Surface.Contracts;

var envelope = new AdaptiveCardSurfaceEnvelope
{
    Id = "approval-card",
    Card = JsonDocument.Parse("""
        {
          "type": "AdaptiveCard",
          "version": "1.6",
          "body": [
            { "type": "TextBlock", "text": "Approve ${requestName}?", "weight": "Bolder" }
          ],
          "actions": [
            {
              "type": "Action.Execute",
              "title": "Approve",
              "verb": "approve",
              "data": {
                "routeTo": "both",
                "messageTemplate": "approved {requestId}",
                "requestId": "${requestId}"
              }
            }
          ]
        }
        """).RootElement.Clone(),
    Data = JsonDocument.Parse("""
        {
          "requestId": "REQ-1001",
          "requestName": "Purchase request REQ-1001"
        }
        """).RootElement.Clone()
};

await surfaceService.RenderAsync(envelope, sourceMessage);
