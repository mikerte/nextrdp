namespace NextRdp.Core;

public enum ConfigurationScope
{
    Organization = 0,
    Workspace = 1,
    Folder = 2,
    Connection = 3
}

public sealed record ConfigurationLayer<T>(
    ConfigurationScope Scope,
    string ScopeId,
    bool HasExplicitOverride,
    T Value,
    bool IsLocked = false);

public sealed record ResolvedConfiguration<T>(
    T Value,
    ConfigurationScope? SourceScope,
    string? SourceScopeId,
    bool IsLocked,
    ConfigurationScope? LockedByScope);

public static class ConfigurationInheritance
{
    public static ResolvedConfiguration<T> Resolve<T>(T defaultValue, IEnumerable<ConfigurationLayer<T>> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var ordered = layers.OrderBy(layer => layer.Scope).ToArray();
        var duplicate = ordered.GroupBy(layer => layer.Scope).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Configuration contains more than one {duplicate.Key} layer.", nameof(layers));
        if (ordered.Any(layer => string.IsNullOrWhiteSpace(layer.ScopeId)))
            throw new ArgumentException("Every configuration layer requires a scope identifier.", nameof(layers));

        var value = defaultValue;
        ConfigurationScope? sourceScope = null;
        string? sourceScopeId = null;
        ConfigurationScope? lockedByScope = null;
        foreach (var layer in ordered)
        {
            if (lockedByScope is not null) break;
            if (layer.HasExplicitOverride)
            {
                value = layer.Value;
                sourceScope = layer.Scope;
                sourceScopeId = layer.ScopeId;
            }
            if (layer.IsLocked) lockedByScope = layer.Scope;
        }

        return new(value, sourceScope, sourceScopeId, lockedByScope is not null, lockedByScope);
    }
}
