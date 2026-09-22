namespace SreAgentChat;

public sealed class AgentOptions
{
    public string? SubscriptionId { get; set; }
    public string? ResourceGroup { get; set; }
    public string? AgentName { get; set; }
    public string? AgentEndpoint { get; set; }
    public int PollIntervalSeconds { get; set; } = 3;
    public int ResponseTimeoutSeconds { get; set; } = 300;

    public void Validate()
    {
        if (!string.IsNullOrWhiteSpace(AgentEndpoint)) return;

        if (string.IsNullOrWhiteSpace(SubscriptionId) ||
            string.IsNullOrWhiteSpace(ResourceGroup) ||
            string.IsNullOrWhiteSpace(AgentName))
        {
            throw new InvalidOperationException(
                "Configura Agent:AgentEndpoint, o bien Agent:SubscriptionId, Agent:ResourceGroup y Agent:AgentName.");
        }
    }
}
