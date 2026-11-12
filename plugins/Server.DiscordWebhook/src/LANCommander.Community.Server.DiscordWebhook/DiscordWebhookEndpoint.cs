namespace LANCommander.Community.Server.DiscordWebhook;

public sealed class DiscordWebhookEndpoint
{
    internal Uri Address { get; }

    private DiscordWebhookEndpoint(Uri address) => Address = address;

    public static DiscordWebhookEndpoint Parse(string value)
    {
        const string error = "Use an HTTPS Discord incoming webhook URL without query, fragment or credentials.";
        if (string.IsNullOrEmpty(value) || value.Length > 2048 ||
            value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            value.Contains('%') || value.Contains('\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var address) ||
            address.Scheme != Uri.UriSchemeHttps || !address.IsDefaultPort ||
            address.Host is not ("discord.com" or "discordapp.com") ||
            address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new ArgumentException(error);

        var parts = address.AbsolutePath.Split('/');
        var offset = parts.Length == 6 && parts[2] == "v10" ? 1 : 0;
        if (parts.Length != 5 + offset || parts[0] != "" || parts[1] != "api" ||
            parts[2 + offset] != "webhooks" ||
            parts[3 + offset].Length is < 1 or > 20 ||
            !parts[3 + offset].All(char.IsAsciiDigit) ||
            parts[4 + offset].Length is < 1 or > 256 ||
            !parts[4 + offset].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new ArgumentException(error);

        // Reject normalization such as /../ rather than accepting a different path.
        if (value != address.GetLeftPart(UriPartial.Authority) + address.AbsolutePath)
            throw new ArgumentException(error);

        return new DiscordWebhookEndpoint(address);
    }

    public override string ToString() => "[Discord webhook endpoint redacted]";
}
