using System.Text.Json;

namespace NextRdp.Platform.Application;

public enum PolicyValueKind
{
    Boolean,
    WholeNumber,
    Text,
    TextList
}

public sealed record PolicyFieldDefinition(string Name, PolicyValueKind Kind, bool Lockable, long? Minimum = null, long? Maximum = null, IReadOnlySet<string>? AllowedValues = null);

public static class PolicySchema
{
    private static readonly Dictionary<string, PolicyFieldDefinition> FieldsValue =
        new Dictionary<string, PolicyFieldDefinition>(StringComparer.Ordinal)
        {
            ["rdp.requireNla"] = new("rdp.requireNla", PolicyValueKind.Boolean, true),
            ["rdp.administrativeSession"] = new("rdp.administrativeSession", PolicyValueKind.Boolean, true),
            ["rdp.gatewayUsage"] = new("rdp.gatewayUsage", PolicyValueKind.Text, true, AllowedValues: Allowed("Never", "Always", "Detect")),
            ["rdp.gatewayHost"] = new("rdp.gatewayHost", PolicyValueKind.Text, true),
            ["rdp.gatewayPort"] = new("rdp.gatewayPort", PolicyValueKind.WholeNumber, true, 1, 65535),
            ["display.mode"] = new("display.mode", PolicyValueKind.Text, true, AllowedValues: Allowed("Embedded", "SeparateWindow", "FullScreen", "AllMonitors", "SelectedMonitors")),
            ["display.width"] = new("display.width", PolicyValueKind.WholeNumber, false, 320, 16384),
            ["display.height"] = new("display.height", PolicyValueKind.WholeNumber, false, 200, 16384),
            ["display.colorDepth"] = new("display.colorDepth", PolicyValueKind.WholeNumber, true, AllowedValues: new HashSet<string>(["15", "16", "24", "32"], StringComparer.Ordinal)),
            ["performance.preset"] = new("performance.preset", PolicyValueKind.Text, true, AllowedValues: Allowed("Auto", "Lan", "Broadband", "LowBandwidth")),
            ["privacy.thumbnailMode"] = new("privacy.thumbnailMode", PolicyValueKind.Text, true, AllowedValues: Allowed("Disabled", "Enabled", "PrivacyLocked")),
            ["input.shortcutRouting"] = new("input.shortcutRouting", PolicyValueKind.Text, true, AllowedValues: Allowed("Local", "RemoteWhenFocused", "RemoteInFullScreen")),
            ["input.allowSas"] = new("input.allowSas", PolicyValueKind.Boolean, true),
            ["redirection.microphone"] = new("redirection.microphone", PolicyValueKind.Boolean, true),
            ["redirection.camera"] = new("redirection.camera", PolicyValueKind.Boolean, true),
            ["redirection.comPorts"] = new("redirection.comPorts", PolicyValueKind.Boolean, true),
            ["redirection.lptPorts"] = new("redirection.lptPorts", PolicyValueKind.Boolean, true),
            ["redirection.usb"] = new("redirection.usb", PolicyValueKind.Boolean, true),
            ["redirection.smartCards"] = new("redirection.smartCards", PolicyValueKind.Boolean, true),
            ["redirection.printers"] = new("redirection.printers", PolicyValueKind.Boolean, true),
            ["redirection.drives"] = new("redirection.drives", PolicyValueKind.Boolean, true),
            ["redirection.audio"] = new("redirection.audio", PolicyValueKind.Boolean, true),
            ["redirection.clipboard"] = new("redirection.clipboard", PolicyValueKind.Text, true, AllowedValues: Allowed("Disabled", "ClientToRemote", "RemoteToClient", "Bidirectional")),
            ["redirection.fileTransfer"] = new("redirection.fileTransfer", PolicyValueKind.Text, true, AllowedValues: Allowed("Disabled", "ClientToRemote", "RemoteToClient", "Bidirectional")),
            ["session.timeoutMinutes"] = new("session.timeoutMinutes", PolicyValueKind.WholeNumber, true, 0, 10080),
            ["session.reconnectAttempts"] = new("session.reconnectAttempts", PolicyValueKind.WholeNumber, true, 0, 20),
            ["client.minimumVersion"] = new("client.minimumVersion", PolicyValueKind.Text, true),
            ["client.bulkConnectLimit"] = new("client.bulkConnectLimit", PolicyValueKind.WholeNumber, true, 1, 25)
        };

    public static IReadOnlyDictionary<string, PolicyFieldDefinition> Fields => FieldsValue;

    public static IReadOnlyList<string> Validate(JsonElement values, IReadOnlyCollection<string> locks)
    {
        var errors = new List<string>();
        if (values.ValueKind != JsonValueKind.Object) return ["Policy values must be a JSON object."];

        foreach (var property in values.EnumerateObject())
        {
            if (!FieldsValue.TryGetValue(property.Name, out var definition))
            {
                errors.Add($"Unsupported policy field '{property.Name}'.");
                continue;
            }

            ValidateValue(definition, property.Value, errors);
        }

        foreach (var field in locks)
        {
            if (!FieldsValue.TryGetValue(field, out var definition)) errors.Add($"Cannot lock unsupported policy field '{field}'.");
            else if (!definition.Lockable) errors.Add($"Policy field '{field}' cannot be locked.");
            else if (!values.TryGetProperty(field, out _)) errors.Add($"Locked policy field '{field}' must have a value at the same layer.");
        }

        if (locks.Count != locks.Distinct(StringComparer.Ordinal).Count()) errors.Add("Policy lock names must be unique.");
        return errors;
    }

    private static void ValidateValue(PolicyFieldDefinition definition, JsonElement value, List<string> errors)
    {
        switch (definition.Kind)
        {
            case PolicyValueKind.Boolean when value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False:
                errors.Add($"Policy field '{definition.Name}' must be a boolean.");
                return;
            case PolicyValueKind.WholeNumber when !value.TryGetInt64(out var number):
                errors.Add($"Policy field '{definition.Name}' must be an integer.");
                return;
            case PolicyValueKind.WholeNumber:
                var integer = value.GetInt64();
                if (definition.Minimum is not null && integer < definition.Minimum || definition.Maximum is not null && integer > definition.Maximum)
                    errors.Add($"Policy field '{definition.Name}' is outside the supported range.");
                if (definition.AllowedValues is not null && !definition.AllowedValues.Contains(integer.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    errors.Add($"Policy field '{definition.Name}' has an unsupported value.");
                return;
            case PolicyValueKind.Text when value.ValueKind != JsonValueKind.String:
                errors.Add($"Policy field '{definition.Name}' must be a string.");
                return;
            case PolicyValueKind.Text:
                var text = value.GetString() ?? string.Empty;
                if (text.Length > 2048 || text.Any(char.IsControl)) errors.Add($"Policy field '{definition.Name}' is too long or contains control characters.");
                if (definition.AllowedValues is not null && !definition.AllowedValues.Contains(text)) errors.Add($"Policy field '{definition.Name}' has an unsupported value.");
                if (definition.Name == "client.minimumVersion" && !SemanticVersion.TryParse(text, out _))
                    errors.Add("Policy field 'client.minimumVersion' must be a semantic version such as 2.0.0 or 2.1.0-preview.1.");
                return;
            case PolicyValueKind.TextList when value.ValueKind != JsonValueKind.Array:
                errors.Add($"Policy field '{definition.Name}' must be an array.");
                return;
            case PolicyValueKind.TextList:
                var items = value.EnumerateArray().ToArray();
                if (items.Length > 32 || items.Any(item => item.ValueKind != JsonValueKind.String || (item.GetString()?.Length ?? 0) > 256))
                    errors.Add($"Policy field '{definition.Name}' contains an unsupported array value.");
                return;
        }
    }

    private static HashSet<string> Allowed(params string[] values) => new(values, StringComparer.Ordinal);
}
