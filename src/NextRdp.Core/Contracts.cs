namespace NextRdp.Core;

public interface IConnectionRepository
{
    Task<IReadOnlyList<ConnectionProfile>> ListAsync(CancellationToken cancellationToken = default);
    Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConnectionFolder>> ListFoldersAsync(CancellationToken cancellationToken = default);
    Task SaveFolderAsync(ConnectionFolder folder, CancellationToken cancellationToken = default);
    Task DeleteFolderAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IAuditStore
{
    Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditEvent>> ListRecentAsync(int limit = 500, CancellationToken cancellationToken = default);
    Task<int> CloseInterruptedSessionsAsync(CancellationToken cancellationToken = default);
}

public interface IImportService
{
    Task<ImportPreview> PreviewAsync(string path, CancellationToken cancellationToken = default);
}

public interface IConnectivityDiagnostics
{
    Task<DiagnosticResult> TestAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public interface ILocalExportService
{
    Task ExportInventoryAsync(string path, CancellationToken cancellationToken = default);
    Task CreateRedactedSupportPackageAsync(string path, CancellationToken cancellationToken = default);
}

public interface ICredentialBroker
{
    Task<CredentialReference?> ResolveAsync(ConnectionProfile connection, CancellationToken cancellationToken = default);
}

public interface IPolicyDecisionPoint
{
    Task<PolicyDecision> AuthorizeLaunchAsync(ConnectionProfile connection, CancellationToken cancellationToken = default);
}

public interface IRdpSessionHost : IDisposable
{
    event EventHandler<SessionState>? StateChanged;
    event EventHandler<string>? DiagnosticMessage;
    SessionState State { get; }
    Task ConnectAsync(ConnectionProfile connection, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
