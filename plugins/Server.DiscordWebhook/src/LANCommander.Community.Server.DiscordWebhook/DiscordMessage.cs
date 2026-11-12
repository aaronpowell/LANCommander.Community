using System.Text.Json;
using System.Text.Json.Serialization;

namespace LANCommander.Community.Server.DiscordWebhook;

public sealed record DiscordMessage(
    string? Content = null,
    IReadOnlyList<DiscordEmbed>? Embeds = null,
    string? Username = null);

public sealed record DiscordEmbed(
    string? Title = null,
    string? Description = null,
    int? Color = null,
    IReadOnlyList<DiscordEmbedField>? Fields = null,
    string? Footer = null,
    string? Author = null);

public sealed record DiscordEmbedField(string Name, string Value, bool Inline = false);

public static class DiscordPayload
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static byte[] Serialize(DiscordMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        CheckText(message.Content, 2000, "Content");
        CheckText(message.Username, 80, "Username");
        var embeds = message.Embeds?.ToArray() ?? [];
        if (embeds.Length > 10)
            throw new ArgumentException("At most 10 embeds are allowed.");
        if (string.IsNullOrWhiteSpace(message.Content) && embeds.Length == 0)
            throw new ArgumentException("Content or at least one embed is required.");

        var total = 0;
        var payloadEmbeds = embeds.Select(embed =>
        {
            if (embed is null)
                throw new ArgumentException("Embeds cannot contain null.");
            CheckText(embed.Title, 256, "Embed title");
            CheckText(embed.Description, 4096, "Embed description");
            CheckText(embed.Footer, 2048, "Embed footer");
            CheckText(embed.Author, 256, "Embed author");
            if (embed.Color is < 0 or > 0xFFFFFF)
                throw new ArgumentException("Embed color must be a 24-bit RGB value.");
            var fields = embed.Fields?.ToArray() ?? [];
            if (fields.Length > 25)
                throw new ArgumentException("At most 25 fields per embed are allowed.");
            var textLength = (embed.Title?.Length ?? 0) + (embed.Description?.Length ?? 0) +
                (embed.Footer?.Length ?? 0) + (embed.Author?.Length ?? 0);
            var payloadFields = fields.Select(field =>
            {
                if (field is null || string.IsNullOrWhiteSpace(field.Name) || string.IsNullOrWhiteSpace(field.Value))
                    throw new ArgumentException("Embed fields require a name and value.");
                CheckText(field.Name, 256, "Embed field name");
                CheckText(field.Value, 1024, "Embed field value");
                textLength += field.Name.Length + field.Value.Length;
                return new { name = field.Name, value = field.Value, inline = field.Inline };
            }).ToArray();
            if (textLength == 0)
                throw new ArgumentException("Each embed must contain text.");
            total += textLength;
            return new
            {
                title = embed.Title,
                description = embed.Description,
                color = embed.Color,
                fields = payloadFields.Length == 0 ? null : payloadFields,
                footer = embed.Footer is null ? null : new { text = embed.Footer },
                author = embed.Author is null ? null : new { name = embed.Author }
            };
        }).ToArray();
        if (total > 6000)
            throw new ArgumentException("Combined embed text must not exceed 6000 characters.");

        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            content = message.Content,
            username = message.Username,
            embeds = payloadEmbeds.Length == 0 ? null : payloadEmbeds,
            allowed_mentions = new { parse = Array.Empty<string>() }
        }, Options);
    }

    private static void CheckText(string? value, int maximum, string field)
    {
        if (value is not null && (value.Length == 0 || value.Length > maximum))
            throw new ArgumentException($"{field} must contain 1-{maximum} characters when supplied.");
    }
}
