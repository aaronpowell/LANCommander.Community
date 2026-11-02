namespace LANCommander.Community.Launcher.MQTT.Transport;

public sealed record MqttConnectionSettings(
    string Host,
    int Port,
    string ClientId,
    string TopicPrefix,
    bool UseTls = false)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new InvalidOperationException("An MQTT broker hostname is required.");
        if (Port is < 1 or > 65535)
            throw new InvalidOperationException("The MQTT broker port must be between 1 and 65535.");
        if (string.IsNullOrEmpty(ClientId) || ClientId.Length is < 8 or > 64 || ClientId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new InvalidOperationException("ClientId must be 8-64 ASCII letters, digits, hyphens, or underscores.");

        var segments = (TopicPrefix ?? "").Split('/');
        if (segments.Length < 2 || segments.Any(segment =>
                segment.Length == 0 || segment.Contains('+') || segment.Contains('#')))
            throw new InvalidOperationException("TopicPrefix must contain at least two literal MQTT topic segments.");
    }
}

public sealed record MqttCredentials(string Username, string Password);
