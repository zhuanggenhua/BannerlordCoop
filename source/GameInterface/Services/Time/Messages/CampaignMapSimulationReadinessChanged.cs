using Common.Messaging;

namespace GameInterface.Services.Time.Messages;

public record CampaignMapSimulationReadinessChanged(bool IsMapReady) : IEvent;
