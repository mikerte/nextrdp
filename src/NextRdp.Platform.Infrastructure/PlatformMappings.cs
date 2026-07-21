using NextRdp.Platform.Application;
using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Infrastructure;

internal static class PlatformMappings
{
    public static OrganizationDto ToDto(this Organization entity) =>
        new(entity.Id, entity.Name, entity.Slug, entity.Revision, entity.UpdatedAtUtc);

    public static WorkspaceDto ToDto(this Workspace entity) =>
        new(entity.Id, entity.OrganizationId, entity.Name, entity.Slug, entity.Description, entity.Revision, entity.UpdatedAtUtc);

    public static FolderDto ToDto(this ManagedFolder entity) =>
        new(entity.Id, entity.OrganizationId, entity.WorkspaceId, entity.ParentId, entity.Name, entity.SortOrder, entity.Revision, entity.UpdatedAtUtc);

    public static ManagedConnectionDto ToDto(this ManagedConnection entity) =>
        new(
            entity.Id,
            entity.OrganizationId,
            entity.WorkspaceId,
            entity.FolderId,
            entity.OwnerUserId,
            entity.Visibility,
            entity.Name,
            entity.Host,
            entity.Port,
            entity.UsernameHint,
            entity.ProvisioningKey,
            entity.Environment,
            entity.Notes,
            entity.Tags.ToArray(),
            entity.Metadata.ToDto(),
            entity.Settings.ToDto(),
            entity.Revision,
            entity.UpdatedAtUtc);

    public static ConnectionMetadataDto ToDto(this ManagedConnectionMetadata value) =>
        new(
            value.BusinessUnit,
            value.ApplicationOwner,
            value.ServiceOwner,
            value.Criticality,
            value.Geography,
            value.DataClassification,
            value.MaintenanceWindow,
            value.SupportGroup,
            value.EscalationContact,
            value.AssetId,
            value.CmdbId,
            value.OperatingSystem,
            value.MonitoringUrl,
            value.DocumentationUrl,
            value.TicketingUrl);

    public static ManagedConnectionMetadata ToDomain(this ConnectionMetadataDto value) =>
        new()
        {
            BusinessUnit = value.BusinessUnit.Trim(),
            ApplicationOwner = value.ApplicationOwner.Trim(),
            ServiceOwner = value.ServiceOwner.Trim(),
            Criticality = value.Criticality.Trim(),
            Geography = value.Geography.Trim(),
            DataClassification = value.DataClassification.Trim(),
            MaintenanceWindow = value.MaintenanceWindow.Trim(),
            SupportGroup = value.SupportGroup.Trim(),
            EscalationContact = value.EscalationContact.Trim(),
            AssetId = value.AssetId.Trim(),
            CmdbId = value.CmdbId.Trim(),
            OperatingSystem = value.OperatingSystem.Trim(),
            MonitoringUrl = value.MonitoringUrl.Trim(),
            DocumentationUrl = value.DocumentationUrl.Trim(),
            TicketingUrl = value.TicketingUrl.Trim()
        };

    public static ManagedRdpSettingsDto ToDto(this ManagedRdpSettings value) =>
        new(
            value.Kind,
            value.RequireNetworkLevelAuthentication,
            value.AdministrativeSession,
            value.BrokerLoadBalanceInfo,
            value.GatewayUsage,
            value.GatewayHost,
            value.GatewayPort,
            value.RemoteAppProgram,
            value.RemoteAppWorkingDirectory,
            value.RemoteAppArguments,
            value.DisplayMode,
            value.SelectedMonitorIds.ToArray(),
            value.DesktopWidth,
            value.DesktopHeight,
            value.ColorDepth,
            value.PerformancePreset,
            value.ShowConnectionQuality,
            value.ThumbnailMode,
            value.ShortcutRouting,
            value.AllowSecureAttentionSequence,
            value.RedirectMicrophone,
            value.RedirectCamera,
            value.RedirectComPorts,
            value.RedirectLptPorts,
            value.RedirectUsbDevices,
            value.RedirectSmartCards,
            value.RedirectPrinters,
            value.RedirectDrives,
            value.RedirectAudio,
            value.ClipboardDirection,
            value.FileTransferDirection,
            value.SessionTimeoutMinutes,
            value.ReconnectAttempts);

    public static ManagedRdpSettings ToDomain(this ManagedRdpSettingsDto value) =>
        new()
        {
            Kind = value.Kind,
            RequireNetworkLevelAuthentication = value.RequireNetworkLevelAuthentication,
            AdministrativeSession = value.AdministrativeSession,
            BrokerLoadBalanceInfo = value.BrokerLoadBalanceInfo.Trim(),
            GatewayUsage = value.GatewayUsage,
            GatewayHost = value.GatewayHost.Trim(),
            GatewayPort = value.GatewayPort,
            RemoteAppProgram = value.RemoteAppProgram.Trim(),
            RemoteAppWorkingDirectory = value.RemoteAppWorkingDirectory.Trim(),
            RemoteAppArguments = value.RemoteAppArguments,
            DisplayMode = value.DisplayMode,
            SelectedMonitorIds = value.SelectedMonitorIds.ToList(),
            DesktopWidth = value.DesktopWidth,
            DesktopHeight = value.DesktopHeight,
            ColorDepth = value.ColorDepth,
            PerformancePreset = value.PerformancePreset,
            ShowConnectionQuality = value.ShowConnectionQuality,
            ThumbnailMode = value.ThumbnailMode,
            ShortcutRouting = value.ShortcutRouting,
            AllowSecureAttentionSequence = value.AllowSecureAttentionSequence,
            RedirectMicrophone = value.RedirectMicrophone,
            RedirectCamera = value.RedirectCamera,
            RedirectComPorts = value.RedirectComPorts,
            RedirectLptPorts = value.RedirectLptPorts,
            RedirectUsbDevices = value.RedirectUsbDevices,
            RedirectSmartCards = value.RedirectSmartCards,
            RedirectPrinters = value.RedirectPrinters,
            RedirectDrives = value.RedirectDrives,
            RedirectAudio = value.RedirectAudio,
            ClipboardDirection = value.ClipboardDirection,
            FileTransferDirection = value.FileTransferDirection,
            SessionTimeoutMinutes = value.SessionTimeoutMinutes,
            ReconnectAttempts = value.ReconnectAttempts
        };
}
