using System.Text.Json;
using System.Buffers.Binary;
using System.Security.Cryptography;
using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Application;

public sealed record ConfigurationLayerInput(
    ConfigurationScopeType ScopeType,
    Guid ScopeId,
    int Version,
    JsonElement Values,
    IReadOnlySet<string> LockedFields);

public sealed class ConfigurationResolver
{
    public static EffectivePolicy Resolve(
        Guid organizationId,
        Guid workspaceId,
        Guid? folderId,
        Guid? connectionId,
        IReadOnlyList<ConfigurationLayerInput> layers)
    {
        var expectedOrder = new[]
        {
            ConfigurationScopeType.Organization,
            ConfigurationScopeType.Workspace,
            ConfigurationScopeType.Folder,
            ConfigurationScopeType.Connection
        };
        var sorted = layers.OrderBy(layer => Array.IndexOf(expectedOrder, layer.ScopeType)).ToArray();
        var fields = new Dictionary<string, EffectivePolicyField>(StringComparer.Ordinal);
        var locks = new Dictionary<string, (ConfigurationScopeType Scope, Guid ScopeId)>(StringComparer.Ordinal);
        var conflicts = new List<string>();
        using var revisionHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> revisionPart = stackalloc byte[20];

        foreach (var layer in sorted)
        {
            var validation = PolicySchema.Validate(layer.Values, layer.LockedFields);
            if (validation.Count > 0) throw PlatformProblemException.Validation(validation);
            revisionPart.Clear();
            layer.ScopeId.TryWriteBytes(revisionPart);
            BinaryPrimitives.WriteInt32BigEndian(revisionPart[16..], layer.Version);
            revisionHash.AppendData(revisionPart);

            foreach (var property in layer.Values.EnumerateObject())
            {
                if (locks.TryGetValue(property.Name, out var lockedBy))
                {
                    conflicts.Add($"{property.Name} is locked by {lockedBy.Scope} {lockedBy.ScopeId:D}; {layer.ScopeType} {layer.ScopeId:D} cannot override it.");
                    continue;
                }

                var isLocked = layer.LockedFields.Contains(property.Name);
                fields[property.Name] = new EffectivePolicyField(property.Name, property.Value.GetRawText(), layer.ScopeType, layer.ScopeId, isLocked);
                if (isLocked) locks[property.Name] = (layer.ScopeType, layer.ScopeId);
            }
        }

        Span<byte> digest = stackalloc byte[32];
        revisionHash.GetHashAndReset(digest);
        var revision = BinaryPrimitives.ReadInt64BigEndian(digest) & long.MaxValue;
        return new EffectivePolicy(organizationId, workspaceId, folderId, connectionId, revision, fields, conflicts);
    }
}
