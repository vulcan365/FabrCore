using FabrCore.Core;
using FabrCore.Core.Streaming;

namespace FabrCore.Host.Tests
{
    [TestClass]
    public sealed class EventStreamSubscriptionTests
    {
        [TestMethod]
        public void ForCreatesSubscriptionThatMatchesEventRouting()
        {
            var subscription = EventStreamSubscription.For(
                "velo-itinerary",
                "squad-velo-travel-desk-itinerary-event");

            Assert.AreEqual(StreamConstants.ProviderName, subscription.Provider);
            Assert.AreEqual("velo-itinerary", subscription.Namespace);
            Assert.AreEqual("squad-velo-travel-desk-itinerary-event", subscription.Channel);
            Assert.AreEqual("velo-itinerary.squad-velo-travel-desk-itinerary-event", subscription.ToString());
        }

        [TestMethod]
        public void FromEventUsesNamespaceAndChannel()
        {
            var message = new EventMessage
            {
                Namespace = "velo-itinerary",
                Channel = "itinerary-event-agent",
                Type = "velo.itinerary.created"
            };

            var subscription = EventStreamSubscription.From(message);

            Assert.AreEqual(message.Namespace, subscription.Namespace);
            Assert.AreEqual(message.Channel, subscription.Channel);
        }

        [TestMethod]
        public void ToStreamNameRejectsDotsInsideParts()
        {
            var subscription = new EventStreamSubscription
            {
                Namespace = "velo.itinerary",
                Channel = "itinerary-event-agent"
            };

            var ex = Assert.ThrowsExactly<ArgumentException>(() => subscription.ToStreamName());
            StringAssert.Contains(ex.Message, "cannot contain dots");
        }

        [TestMethod]
        public void ToStreamNameForEventUsesCustomNamespace()
        {
            var message = new EventMessage
            {
                Namespace = "velo-itinerary",
                Channel = "itinerary-event-agent"
            };

            var streamName = EventStreamSubscription.ToStreamName(message);

            Assert.AreEqual(StreamConstants.ProviderName, streamName.Provider);
            Assert.AreEqual("velo-itinerary", streamName.Namespace);
            Assert.AreEqual("itinerary-event-agent", streamName.Handle);
        }

        [TestMethod]
        public void ToStreamNameForEventUsesDefaultAgentEventNamespaceWhenNamespaceIsEmpty()
        {
            var message = new EventMessage
            {
                Channel = "user1:itinerary-agent"
            };

            var streamName = EventStreamSubscription.ToStreamName(message);

            Assert.AreEqual(StreamConstants.AgentEventNamespace, streamName.Namespace);
            Assert.AreEqual("user1:itinerary-agent", streamName.Handle);
        }

        [TestMethod]
        public void ToStreamNameRejectsReservedChatNamespace()
        {
            var subscription = new EventStreamSubscription
            {
                Namespace = StreamConstants.AgentChatNamespace,
                Channel = "itinerary-event-agent"
            };

            var ex = Assert.ThrowsExactly<ArgumentException>(() => subscription.ToStreamName());
            StringAssert.Contains(ex.Message, "reserved AgentChat namespace");
        }

        [TestMethod]
        public void AgentConfigurationStreamsAreTypedSubscriptions()
        {
            var config = new AgentConfiguration
            {
                Streams =
                [
                    EventStreamSubscription.For("velo-itinerary", "itinerary-event-agent")
                ]
            };

            Assert.AreEqual("velo-itinerary", config.Streams[0].Namespace);
            Assert.AreEqual("itinerary-event-agent", config.Streams[0].Channel);
        }
    }
}
