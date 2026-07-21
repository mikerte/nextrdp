export type Session = {
  principalId: string;
  organizationId: string | null;
  roles: string[];
  groups: string[];
  mfaSatisfied: boolean;
  isServiceAccount: boolean;
};

export type Organization = { id: string; name: string; slug: string; revision: number; updatedAtUtc: string };
export type Workspace = { id: string; organizationId: string; name: string; slug: string; description: string; revision: number; updatedAtUtc: string };
export type Folder = { id: string; organizationId: string; workspaceId: string; parentId: string | null; name: string; sortOrder: number; revision: number; updatedAtUtc: string };
export type User = { id: string; displayName: string; emailHint: string; status: string; lastAuthenticatedAtUtc: string; sessionVersion: number };
export type Membership = { id: string; organizationId: string; workspaceId: string | null; userId: string; role: string; status: string; sourceGroup: string; revision: number; updatedAtUtc: string };
export type GroupRoleMapping = { id: string; organizationId: string; workspaceId: string | null; groupClaim: string; role: string; enabled: boolean; revision: number; updatedAtUtc: string };

export type Metadata = {
  businessUnit: string; applicationOwner: string; serviceOwner: string; criticality: string; geography: string;
  dataClassification: string; maintenanceWindow: string; supportGroup: string; escalationContact: string; assetId: string;
  cmdbId: string; operatingSystem: string; monitoringUrl: string; documentationUrl: string; ticketingUrl: string;
};

export type RdpSettings = {
  kind: 'Desktop' | 'RemoteApp'; requireNetworkLevelAuthentication: boolean; administrativeSession: boolean;
  brokerLoadBalanceInfo: string; gatewayUsage: 'Never' | 'Always' | 'Detect'; gatewayHost: string; gatewayPort: number;
  remoteAppProgram: string; remoteAppWorkingDirectory: string; remoteAppArguments: string;
  displayMode: 'Embedded' | 'SeparateWindow' | 'FullScreen' | 'AllMonitors' | 'SelectedMonitors'; selectedMonitorIds: number[];
  desktopWidth: number | null; desktopHeight: number | null; colorDepth: number; performancePreset: 'Auto' | 'Lan' | 'Broadband' | 'LowBandwidth';
  showConnectionQuality: boolean; thumbnailMode: 'Disabled' | 'Enabled' | 'PrivacyLocked'; shortcutRouting: 'Local' | 'RemoteWhenFocused' | 'RemoteInFullScreen';
  allowSecureAttentionSequence: boolean; redirectMicrophone: boolean; redirectCamera: boolean; redirectComPorts: boolean; redirectLptPorts: boolean;
  redirectUsbDevices: boolean; redirectSmartCards: boolean; redirectPrinters: boolean; redirectDrives: boolean; redirectAudio: boolean;
  clipboardDirection: 'Disabled' | 'ClientToRemote' | 'RemoteToClient' | 'Bidirectional'; fileTransferDirection: 'Disabled' | 'ClientToRemote' | 'RemoteToClient' | 'Bidirectional';
  sessionTimeoutMinutes: number; reconnectAttempts: number;
};

export type Connection = {
  id: string; organizationId: string; workspaceId: string; folderId: string | null; ownerUserId: string | null;
  visibility: 'Personal' | 'Shared'; name: string; host: string; port: number; usernameHint: string; provisioningKey: string;
  environment: 'Development' | 'Test' | 'Lab' | 'DisasterRecovery' | 'Production'; notes: string; tags: string[]; metadata: Metadata; settings: RdpSettings;
  revision: number; updatedAtUtc: string;
};

export type AuditEvent = { id: string; actorAlias: string; action: string; resourceType: string; outcome: string; correlationId: string; occurredAtUtc: string };
export type ServiceAccount = { id: string; name: string; clientId: string; workspaceId: string | null; scopes: string[]; expiresAtUtc: string | null; revokedAtUtc: string | null };
export type DynamicGroup = { id: string; name: string; rules: { field: string; operator: string; value: string }[]; matchingConnectionIds: string[]; revision: number; updatedAtUtc: string };
export type ConnectionTemplate = { id: string; name: string; description: string; defaults: { environment: Connection['environment']; notes: string; tags: string[]; metadata: Metadata; settings: RdpSettings }; revision: number; updatedAtUtc: string };
export type UserWorkspaceState = { organizationId: string; workspaceId: string; userId: string; pinnedFolderIds: string[]; layout: { navigationWidth: number; inventorySort: string; theme: string; reopenLastWorkspace: boolean }; savedWorkspaces: { id: string; name: string; connectionIds: string[]; displayMode: RdpSettings['displayMode'] }[]; tabGroups: { id: string; name: string; color: string; connectionIds: string[] }[]; revision: number; updatedAtUtc: string };
export type Health = { status: string; durationMs: number; checks: { name: string; status: string; durationMs: number }[] };

export const emptyMetadata: Metadata = {
  businessUnit: '', applicationOwner: '', serviceOwner: '', criticality: 'Standard', geography: '', dataClassification: 'Internal',
  maintenanceWindow: '', supportGroup: '', escalationContact: '', assetId: '', cmdbId: '', operatingSystem: 'Windows',
  monitoringUrl: '', documentationUrl: '', ticketingUrl: ''
};

export const defaultRdpSettings: RdpSettings = {
  kind: 'Desktop', requireNetworkLevelAuthentication: true, administrativeSession: false, brokerLoadBalanceInfo: '', gatewayUsage: 'Never', gatewayHost: '', gatewayPort: 443,
  remoteAppProgram: '', remoteAppWorkingDirectory: '', remoteAppArguments: '', displayMode: 'Embedded', selectedMonitorIds: [], desktopWidth: null, desktopHeight: null,
  colorDepth: 32, performancePreset: 'Auto', showConnectionQuality: true, thumbnailMode: 'Disabled', shortcutRouting: 'RemoteInFullScreen', allowSecureAttentionSequence: true,
  redirectMicrophone: false, redirectCamera: false, redirectComPorts: false, redirectLptPorts: false, redirectUsbDevices: false, redirectSmartCards: false,
  redirectPrinters: true, redirectDrives: true, redirectAudio: true, clipboardDirection: 'Bidirectional', fileTransferDirection: 'Bidirectional', sessionTimeoutMinutes: 0, reconnectAttempts: 3
};
