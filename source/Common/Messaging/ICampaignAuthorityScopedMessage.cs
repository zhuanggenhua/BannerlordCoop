namespace Common.Messaging;

/// <summary>Shared campaign state delta that must be fenced by the current authority lease.</summary>
public interface ICampaignAuthorityScopedMessage : IMessage
{
}
