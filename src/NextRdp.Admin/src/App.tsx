import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Badge, Button, Card, Checkbox, Field, Input, MessageBar, MessageBarBody, Select, Spinner, Textarea
} from '@fluentui/react-components';
import { api, ApiError } from './api';
import {
  type AuditEvent, type Connection, type ConnectionTemplate, type DynamicGroup, type Folder, type GroupRoleMapping, type Health, type Membership, type Organization, type ServiceAccount,
  type Session, type User, type UserWorkspaceState, type Workspace, defaultRdpSettings, emptyMetadata
} from './models';

type View = 'dashboard' | 'inventory' | 'productivity' | 'configuration' | 'access' | 'audit' | 'system';
const views: { id: View; label: string; description: string }[] = [
  { id: 'dashboard', label: 'Overview', description: 'Workspace status and shortcuts' },
  { id: 'inventory', label: 'Inventory', description: 'Folders, shared and personal connections' },
  { id: 'productivity', label: 'Productivity', description: 'Groups, templates, pins, workspaces and bulk safety' },
  { id: 'configuration', label: 'Configuration', description: 'Draft, publish, history and rollback' },
  { id: 'access', label: 'Access', description: 'Roles, memberships and service accounts' },
  { id: 'audit', label: 'Audit', description: 'Redacted administrative activity' },
  { id: 'system', label: 'System health', description: 'Control-plane dependency readiness' }
];

export function App() {
  const [session, setSession] = useState<Session | null>(null);
  const [organizations, setOrganizations] = useState<Organization[]>([]);
  const [workspaces, setWorkspaces] = useState<Workspace[]>([]);
  const [organizationId, setOrganizationId] = useState('');
  const [workspaceId, setWorkspaceId] = useState('');
  const [view, setView] = useState<View>('dashboard');
  const [busy, setBusy] = useState(true);
  const [signInRequired, setSignInRequired] = useState(false);
  const [error, setError] = useState('');
  const [paletteOpen, setPaletteOpen] = useState(false);

  const loadOrganizations = useCallback(async () => {
    const items = await api.get<Organization[]>('/api/v1/organizations');
    setOrganizations(items);
    setOrganizationId(current => current && items.some(item => item.id === current) ? current : items[0]?.id ?? '');
  }, []);

  useEffect(() => {
    const initialize = async () => {
      try {
        const identity = await api.get<Session>('/auth/me');
        setSession(identity);
        await loadOrganizations();
      } catch (caught) {
        if (caught instanceof ApiError && (caught.status === 401 || caught.status === 403)) setSignInRequired(true);
        else setError(message(caught));
      } finally { setBusy(false); }
    };
    void initialize();
  }, [loadOrganizations]);

  useEffect(() => {
    if (!organizationId) { setWorkspaces([]); setWorkspaceId(''); return; }
    void api.get<Workspace[]>(`/api/v1/organizations/${organizationId}/workspaces`).then(items => {
      setWorkspaces(items);
      setWorkspaceId(current => current && items.some(item => item.id === current) ? current : items[0]?.id ?? '');
    }).catch(caught => setError(message(caught)));
  }, [organizationId]);

  useEffect(() => {
    const keydown = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') { event.preventDefault(); setPaletteOpen(open => !open); }
      if (event.key === 'Escape') setPaletteOpen(false);
    };
    window.addEventListener('keydown', keydown);
    return () => window.removeEventListener('keydown', keydown);
  }, []);

  const selectedOrganization = organizations.find(item => item.id === organizationId);
  const selectedWorkspace = workspaces.find(item => item.id === workspaceId);
  if (busy) return <Centered><Spinner size="huge" label="Loading NextRDP administration" /></Centered>;
  if (signInRequired) return <SignIn />;

  return <div className="app-shell">
    <header className="topbar">
      <div className="brand"><span className="brand-mark" aria-hidden="true">N</span><div><strong>NextRDP</strong><span>Administration</span></div></div>
      <div className="scope-pickers">
        <label>Organization<Select aria-label="Organization" value={organizationId} onChange={event => setOrganizationId(event.target.value)}>
          <option value="">Choose organization</option>{organizations.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
        </Select></label>
        <label>Workspace<Select aria-label="Workspace" value={workspaceId} disabled={!organizationId} onChange={event => setWorkspaceId(event.target.value)}>
          <option value="">Choose workspace</option>{workspaces.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
        </Select></label>
      </div>
      <Button appearance="subtle" onClick={() => setPaletteOpen(true)} aria-keyshortcuts="Control+K">Commands <kbd>Ctrl K</kbd></Button>
      <div className="identity"><span>{session?.roles[0] ?? 'User'}</span><small>MFA {session?.mfaSatisfied ? 'verified' : 'not reported'}</small></div>
    </header>
    <aside className="sidebar">
      <nav aria-label="Administration navigation">{views.map(item => <button key={item.id} className={view === item.id ? 'nav-item active' : 'nav-item'} onClick={() => setView(item.id)} aria-current={view === item.id ? 'page' : undefined}>
        <strong>{item.label}</strong><span>{item.description}</span>
      </button>)}</nav>
      <div className="sidebar-footer"><span className="status-dot" /> Phase 2 control plane</div>
    </aside>
    <main id="main-content" className="content" tabIndex={-1}>
      {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody><Button appearance="transparent" onClick={() => setError('')}>Dismiss</Button></MessageBar>}
      {!selectedOrganization ? <OrganizationSetup onCreated={async () => { await loadOrganizations(); }} onError={setError} /> :
       !selectedWorkspace && view !== 'access' && view !== 'audit' && view !== 'system' ? <WorkspaceSetup organization={selectedOrganization} onCreated={async () => {
         const items = await api.get<Workspace[]>(`/api/v1/organizations/${organizationId}/workspaces`); setWorkspaces(items); setWorkspaceId(items.at(-1)?.id ?? '');
       }} onError={setError} /> : <>
        {view === 'dashboard' && selectedWorkspace && <Dashboard organization={selectedOrganization} workspace={selectedWorkspace} onNavigate={setView} />}
        {view === 'inventory' && selectedWorkspace && <Inventory organization={selectedOrganization} workspace={selectedWorkspace} onError={setError} />}
        {view === 'productivity' && selectedWorkspace && <Productivity organization={selectedOrganization} workspace={selectedWorkspace} onError={setError} />}
        {view === 'configuration' && selectedWorkspace && <Configuration organization={selectedOrganization} workspace={selectedWorkspace} onError={setError} />}
        {view === 'access' && <Access organization={selectedOrganization} workspaces={workspaces} onError={setError} />}
        {view === 'audit' && <Audit organization={selectedOrganization} workspace={selectedWorkspace} onError={setError} />}
        {view === 'system' && <SystemHealth onError={setError} />}
      </>}
    </main>
    {paletteOpen && <CommandPalette current={view} onChoose={chosen => { setView(chosen); setPaletteOpen(false); }} onClose={() => setPaletteOpen(false)} />}
  </div>;
}

function SignIn() {
  return <Centered><Card className="signin-card"><div className="brand-mark large" aria-hidden="true">N</div><h1>Sign in to NextRDP</h1><p>Use your organization identity provider. Multi-factor authentication remains delegated to that provider.</p><Button appearance="primary" as="a" href="/auth/login?returnUrl=/">Continue with corporate sign-in</Button></Card></Centered>;
}

function OrganizationSetup({ onCreated, onError }: { onCreated: () => Promise<void>; onError: (error: string) => void }) {
  const [name, setName] = useState(''); const [slug, setSlug] = useState(''); const [saving, setSaving] = useState(false);
  return <Page title="Create the first organization" description="This local control plane has no organization yet. Only a platform administrator with MFA can create one.">
    <Card className="form-card"><Field label="Organization name" required><Input value={name} onChange={(_, data) => { setName(data.value); setSlug(slugify(data.value)); }} /></Field>
      <Field label="URL-safe slug" required><Input value={slug} onChange={(_, data) => setSlug(data.value)} /></Field>
      <Button appearance="primary" disabled={saving || !name || !slug} onClick={async () => { setSaving(true); try { await api.post('/api/v1/organizations', { name, slug }); await onCreated(); } catch (caught) { onError(message(caught)); } finally { setSaving(false); } }}>{saving ? 'Creating…' : 'Create organization'}</Button>
    </Card>
  </Page>;
}

function WorkspaceSetup({ organization, onCreated, onError }: { organization: Organization; onCreated: () => Promise<void>; onError: (error: string) => void }) {
  const [name, setName] = useState(''); const [slug, setSlug] = useState(''); const [description, setDescription] = useState('');
  return <Page title="Create a workspace" description={`Add the first isolated workspace in ${organization.name}.`}><Card className="form-card">
    <Field label="Workspace name" required><Input value={name} onChange={(_, data) => { setName(data.value); setSlug(slugify(data.value)); }} /></Field>
    <Field label="Slug" required><Input value={slug} onChange={(_, data) => setSlug(data.value)} /></Field>
    <Field label="Description"><Textarea value={description} onChange={(_, data) => setDescription(data.value)} /></Field>
    <Button appearance="primary" disabled={!name || !slug} onClick={async () => { try { await api.post(`/api/v1/organizations/${organization.id}/workspaces`, { name, slug, description }); await onCreated(); } catch (caught) { onError(message(caught)); } }}>Create workspace</Button>
  </Card></Page>;
}

function Dashboard({ organization, workspace, onNavigate }: { organization: Organization; workspace: Workspace; onNavigate: (view: View) => void }) {
  return <Page title={workspace.name} description={`${organization.name} · ${workspace.description || 'Shared management workspace'}`} actions={<Badge appearance="tint" color="success">Synchronized management</Badge>}>
    <div className="metric-grid"><Metric label="Configuration revision" value={String(workspace.revision)} /><Metric label="Workspace updated" value={formatDate(workspace.updatedAtUtc)} /><Metric label="Isolation" value="Organization scoped" /><Metric label="Credential model" value="Prompt only" /></div>
    <h2>Continue working</h2><div className="card-grid">{views.slice(1, 5).map(item => <Card key={item.id} className="action-card" onClick={() => onNavigate(item.id)}><h3>{item.label}</h3><p>{item.description}</p><Button appearance="subtle">Open</Button></Card>)}</div>
  </Page>;
}

function Inventory({ organization, workspace, onError }: { organization: Organization; workspace: Workspace; onError: (error: string) => void }) {
  const base = `/api/v1/organizations/${organization.id}/workspaces/${workspace.id}`;
  const [connections, setConnections] = useState<Connection[]>([]); const [folders, setFolders] = useState<Folder[]>([]);
  const [search, setSearch] = useState(''); const [environment, setEnvironment] = useState(''); const [owner, setOwner] = useState('');
  const [selected, setSelected] = useState<Connection | null>(null); const [editing, setEditing] = useState(false); const [loading, setLoading] = useState(true);
  const reload = useCallback(async () => { setLoading(true); try {
    const searchParameter = search.trim() ? `search=${encodeURIComponent(search.trim())}&` : '';
    const [connectionResult, folderResult] = await Promise.all([
      api.get<{ items: Connection[] }>(`${base}/connections?${searchParameter}pageSize=200`), api.get<Folder[]>(`${base}/folders`)
    ]); setConnections(connectionResult.items); setFolders(folderResult);
  } catch (caught) { onError(message(caught)); } finally { setLoading(false); } }, [base, onError, search]);
  useEffect(() => { const timeout = window.setTimeout(() => void reload(), 180); return () => window.clearTimeout(timeout); }, [reload]);
  const filtered = connections.filter(item => (!environment || item.environment === environment) && (!owner || item.metadata.applicationOwner.toLowerCase().includes(owner.toLowerCase())));
  return <Page title="Managed inventory" description="Search authorized shared and personal profiles. No credential material is stored or synchronized." actions={<Button appearance="primary" onClick={() => { setSelected(null); setEditing(true); }}>New connection</Button>}>
    <div className="toolbar"><Input aria-label="Search inventory" placeholder="Search hostname, IP, tag, owner or application" value={search} onChange={(_, data) => setSearch(data.value)} />
      <Select aria-label="Environment filter" value={environment} onChange={event => setEnvironment(event.target.value)}><option value="">All environments</option><option>Production</option><option>Test</option><option>Development</option><option>DisasterRecovery</option><option>Lab</option></Select>
      <Input aria-label="Application owner filter" placeholder="Application owner" value={owner} onChange={(_, data) => setOwner(data.value)} />
      <span>{filtered.length} permitted profiles</span></div>
    {loading ? <Spinner label="Loading inventory" /> : filtered.length === 0 ? <Empty title="No matching connections" detail="Change the filters or create a managed connection." /> : <div className="table-wrap"><table><thead><tr><th>Name</th><th>Target</th><th>Environment</th><th>Owner</th><th>Visibility</th><th>Updated</th></tr></thead><tbody>{filtered.map(item => <tr key={item.id} tabIndex={0} onClick={() => setSelected(item)} onKeyDown={event => { if (event.key === 'Enter') setSelected(item); }} className={selected?.id === item.id ? 'selected' : ''}>
      <td><strong>{item.name}</strong><small>{item.tags.join(', ')}</small></td><td>{item.host}:{item.port}</td><td><Badge color={item.environment === 'Production' ? 'danger' : 'informative'}>{item.environment}</Badge></td><td>{item.metadata.applicationOwner || 'Unassigned'}</td><td>{item.visibility}</td><td>{formatDate(item.updatedAtUtc)}</td>
    </tr>)}</tbody></table></div>}
    {selected && !editing && <Card className="detail-card"><div><h2>{selected.name}</h2><p>{selected.notes || 'No notes'}</p><dl><dt>Folder</dt><dd>{folders.find(folder => folder.id === selected.folderId)?.name ?? 'Root'}</dd><dt>Gateway</dt><dd>{selected.settings.gatewayUsage === 'Never' ? 'Direct' : `${selected.settings.gatewayUsage}: ${selected.settings.gatewayHost}`}</dd><dt>Display</dt><dd>{selected.settings.displayMode}</dd><dt>Criticality</dt><dd>{selected.metadata.criticality}</dd></dl></div><Button onClick={() => setEditing(true)}>Edit</Button></Card>}
    {editing && <ConnectionEditor connection={selected} folders={folders} onCancel={() => setEditing(false)} onSave={async request => { try {
      if (selected) await api.put(`${base}/connections/${selected.id}`, request, selected.revision); else await api.post(`${base}/connections`, request);
      setEditing(false); setSelected(null); await reload();
    } catch (caught) { onError(message(caught)); } }} />}
  </Page>;
}

function ConnectionEditor({ connection, folders, onCancel, onSave }: { connection: Connection | null; folders: Folder[]; onCancel: () => void; onSave: (request: Omit<Connection, 'id' | 'organizationId' | 'workspaceId' | 'ownerUserId' | 'provisioningKey' | 'revision' | 'updatedAtUtc'> & { expectedRevision: null }) => Promise<void> }) {
  const [name, setName] = useState(connection?.name ?? ''); const [host, setHost] = useState(connection?.host ?? ''); const [port, setPort] = useState(connection?.port ?? 3389);
  const [usernameHint, setUsernameHint] = useState(connection?.usernameHint ?? ''); const [folderId, setFolderId] = useState(connection?.folderId ?? '');
  const [visibility, setVisibility] = useState<'Shared' | 'Personal'>(connection?.visibility ?? 'Shared'); const [environment, setEnvironment] = useState<Connection['environment']>(connection?.environment ?? 'Development');
  const [notes, setNotes] = useState(connection?.notes ?? ''); const [tags, setTags] = useState(connection?.tags.join(', ') ?? '');
  const [metadata, setMetadata] = useState(connection?.metadata ?? emptyMetadata);
  const [settings, setSettings] = useState(connection?.settings ?? defaultRdpSettings);
  const [selectedMonitorIdsText, setSelectedMonitorIdsText] = useState(connection?.settings.selectedMonitorIds.join(', ') ?? '');
  const selectedMonitorIds = parseSelectedMonitorIds(selectedMonitorIdsText);
  const selectedMonitorConfigurationValid = settings.displayMode !== 'SelectedMonitors' || selectedMonitorIds !== null;
  const updateMetadata = (field: keyof typeof metadata, value: string) => setMetadata(current => ({ ...current, [field]: value }));
  const updateSettings = <K extends keyof typeof settings,>(field: K, value: (typeof settings)[K]) => setSettings(current => ({ ...current, [field]: value }));
  return <section className="editor" aria-label={connection ? 'Edit managed connection' : 'Create managed connection'}><div className="section-heading"><div><h2>{connection ? 'Edit connection' : 'New managed connection'}</h2><p>Credentials are intentionally absent from this form.</p></div><Button onClick={onCancel}>Cancel</Button></div>
    <div className="form-grid"><Field label="Name" required><Input value={name} onChange={(_, data) => setName(data.value)} /></Field><Field label="Host or IP" required><Input value={host} onChange={(_, data) => setHost(data.value)} /></Field>
      <Field label="Port"><Input type="number" value={String(port)} onChange={(_, data) => setPort(Number(data.value))} /></Field><Field label="Username hint"><Input value={usernameHint} onChange={(_, data) => setUsernameHint(data.value)} /></Field>
      <Field label="Folder"><Select value={folderId} onChange={event => setFolderId(event.target.value)}><option value="">Root</option>{folders.map(folder => <option key={folder.id} value={folder.id}>{folder.name}</option>)}</Select></Field>
      <Field label="Visibility"><Select value={visibility} onChange={event => setVisibility(event.target.value as 'Shared' | 'Personal')}><option>Shared</option><option>Personal</option></Select></Field>
      <Field label="Environment"><Select value={environment} onChange={event => setEnvironment(event.target.value as Connection['environment'])}><option>Development</option><option>Test</option><option>Lab</option><option>DisasterRecovery</option><option>Production</option></Select></Field>
      <Field label="Tags" hint="Comma separated"><Input value={tags} onChange={(_, data) => setTags(data.value)} /></Field><Field label="Application owner"><Input value={metadata.applicationOwner} onChange={(_, data) => updateMetadata('applicationOwner', data.value)} /></Field>
      <Field label="Business unit"><Input value={metadata.businessUnit} onChange={(_, data) => updateMetadata('businessUnit', data.value)} /></Field><Field label="Service owner"><Input value={metadata.serviceOwner} onChange={(_, data) => updateMetadata('serviceOwner', data.value)} /></Field>
      <Field label="Criticality"><Select value={metadata.criticality} onChange={event => updateMetadata('criticality', event.target.value)}><option>Standard</option><option>Important</option><option>Critical</option></Select></Field><Field label="Geography"><Input value={metadata.geography} onChange={(_, data) => updateMetadata('geography', data.value)} /></Field>
      <Field label="Data classification"><Input value={metadata.dataClassification} onChange={(_, data) => updateMetadata('dataClassification', data.value)} /></Field><Field label="Maintenance window"><Input value={metadata.maintenanceWindow} onChange={(_, data) => updateMetadata('maintenanceWindow', data.value)} /></Field>
      <Field label="Support group"><Input value={metadata.supportGroup} onChange={(_, data) => updateMetadata('supportGroup', data.value)} /></Field><Field label="Escalation contact"><Input value={metadata.escalationContact} onChange={(_, data) => updateMetadata('escalationContact', data.value)} /></Field>
      <Field label="Asset ID"><Input value={metadata.assetId} onChange={(_, data) => updateMetadata('assetId', data.value)} /></Field><Field label="CMDB ID"><Input value={metadata.cmdbId} onChange={(_, data) => updateMetadata('cmdbId', data.value)} /></Field>
      <Field label="Operating system"><Input value={metadata.operatingSystem} onChange={(_, data) => updateMetadata('operatingSystem', data.value)} /></Field><Field label="Monitoring URL"><Input type="url" value={metadata.monitoringUrl} onChange={(_, data) => updateMetadata('monitoringUrl', data.value)} /></Field>
      <Field label="Documentation URL"><Input type="url" value={metadata.documentationUrl} onChange={(_, data) => updateMetadata('documentationUrl', data.value)} /></Field><Field label="Ticketing URL"><Input type="url" value={metadata.ticketingUrl} onChange={(_, data) => updateMetadata('ticketingUrl', data.value)} /></Field>
      <Field label="Gateway usage"><Select value={settings.gatewayUsage} onChange={event => updateSettings('gatewayUsage', event.target.value as typeof settings.gatewayUsage)}><option>Never</option><option>Detect</option><option>Always</option></Select></Field>
      <Field label="Gateway host"><Input disabled={settings.gatewayUsage === 'Never'} value={settings.gatewayHost} onChange={(_, data) => updateSettings('gatewayHost', data.value)} /></Field><Field label="Gateway port"><Input type="number" value={String(settings.gatewayPort)} onChange={(_, data) => updateSettings('gatewayPort', Number(data.value))} /></Field>
      <Field label="Broker load-balance information"><Input value={settings.brokerLoadBalanceInfo} onChange={(_, data) => updateSettings('brokerLoadBalanceInfo', data.value)} /></Field>
      <Checkbox label="RemoteApp profile" checked={settings.kind === 'RemoteApp'} onChange={(_, data) => updateSettings('kind', data.checked === true ? 'RemoteApp' : 'Desktop')} /><Checkbox label="Administrative session" checked={settings.administrativeSession} onChange={(_, data) => updateSettings('administrativeSession', data.checked === true)} />
      <Checkbox label="Require Network Level Authentication" checked={settings.requireNetworkLevelAuthentication} onChange={(_, data) => updateSettings('requireNetworkLevelAuthentication', data.checked === true)} />
      {settings.kind === 'RemoteApp' && <><Field label="RemoteApp program" required><Input value={settings.remoteAppProgram} onChange={(_, data) => updateSettings('remoteAppProgram', data.value)} /></Field><Field label="RemoteApp working directory"><Input value={settings.remoteAppWorkingDirectory} onChange={(_, data) => updateSettings('remoteAppWorkingDirectory', data.value)} /></Field><Field label="RemoteApp arguments"><Input value={settings.remoteAppArguments} onChange={(_, data) => updateSettings('remoteAppArguments', data.value)} /></Field></>}
      <Field label="Display mode"><Select value={settings.displayMode} onChange={event => updateSettings('displayMode', event.target.value as typeof settings.displayMode)}><option>Embedded</option><option>SeparateWindow</option><option>FullScreen</option><option>AllMonitors</option><option>SelectedMonitors</option></Select></Field>
      {settings.displayMode === 'SelectedMonitors' && <Field label="Selected monitor IDs" hint="Comma-separated zero-based IDs, for example 0, 1." validationState={selectedMonitorIds === null ? 'error' : 'none'} validationMessage={selectedMonitorIds === null ? 'Enter one or more unique monitor IDs from 0 through 4294967295.' : undefined}><Input value={selectedMonitorIdsText} onChange={(_, data) => setSelectedMonitorIdsText(data.value)} /></Field>}
      <Field label="Desktop width"><Input type="number" value={String(settings.desktopWidth ?? '')} onChange={(_, data) => updateSettings('desktopWidth', data.value ? Number(data.value) : null)} /></Field><Field label="Desktop height"><Input type="number" value={String(settings.desktopHeight ?? '')} onChange={(_, data) => updateSettings('desktopHeight', data.value ? Number(data.value) : null)} /></Field>
      <Field label="Color depth"><Select value={String(settings.colorDepth)} onChange={event => updateSettings('colorDepth', Number(event.target.value))}><option>15</option><option>16</option><option>24</option><option>32</option></Select></Field><Field label="Performance preset"><Select value={settings.performancePreset} onChange={event => updateSettings('performancePreset', event.target.value as typeof settings.performancePreset)}><option>Auto</option><option>Lan</option><option>Broadband</option><option>LowBandwidth</option></Select></Field>
      <Field label="Thumbnail privacy"><Select value={settings.thumbnailMode} onChange={event => updateSettings('thumbnailMode', event.target.value as typeof settings.thumbnailMode)}><option>Disabled</option><option>Enabled</option><option>PrivacyLocked</option></Select></Field><Field label="Shortcut routing"><Select value={settings.shortcutRouting} onChange={event => updateSettings('shortcutRouting', event.target.value as typeof settings.shortcutRouting)}><option>Local</option><option>RemoteWhenFocused</option><option>RemoteInFullScreen</option></Select></Field>
      <Checkbox label="Show connection quality" checked={settings.showConnectionQuality} onChange={(_, data) => updateSettings('showConnectionQuality', data.checked === true)} /><Checkbox label="Allow Secure Attention Sequence" checked={settings.allowSecureAttentionSequence} onChange={(_, data) => updateSettings('allowSecureAttentionSequence', data.checked === true)} />
      {(['redirectMicrophone','redirectCamera','redirectComPorts','redirectLptPorts','redirectUsbDevices','redirectSmartCards','redirectPrinters','redirectDrives','redirectAudio'] as const).map(field => <Checkbox key={field} label={field.replace(/^redirect/, 'Redirect ')} checked={settings[field]} onChange={(_, data) => updateSettings(field, data.checked === true)} />)}
      <Field label="Clipboard direction"><Select value={settings.clipboardDirection} onChange={event => updateSettings('clipboardDirection', event.target.value as typeof settings.clipboardDirection)}><option>Disabled</option><option>ClientToRemote</option><option>RemoteToClient</option><option>Bidirectional</option></Select></Field><Field label="File-transfer direction"><Select value={settings.fileTransferDirection} onChange={event => updateSettings('fileTransferDirection', event.target.value as typeof settings.fileTransferDirection)}><option>Disabled</option><option>ClientToRemote</option><option>RemoteToClient</option><option>Bidirectional</option></Select></Field>
      <Field label="Session timeout (minutes)"><Input type="number" value={String(settings.sessionTimeoutMinutes)} onChange={(_, data) => updateSettings('sessionTimeoutMinutes', Number(data.value))} /></Field><Field label="Reconnect attempts"><Input type="number" value={String(settings.reconnectAttempts)} onChange={(_, data) => updateSettings('reconnectAttempts', Number(data.value))} /></Field>
      <Field label="Notes" className="span-two"><Textarea value={notes} onChange={(_, data) => setNotes(data.value)} /></Field></div>
    <div className="editor-actions"><Button appearance="primary" disabled={!name.trim() || !host.trim() || !selectedMonitorConfigurationValid || (settings.kind === 'RemoteApp' && !settings.remoteAppProgram.trim())} onClick={() => void onSave({
      folderId: folderId || null, visibility, name: name.trim(), host: host.trim(), port, usernameHint: usernameHint.trim(), environment, notes,
      tags: tags.split(',').map(tag => tag.trim()).filter(Boolean), metadata,
      settings: { ...settings, selectedMonitorIds: settings.displayMode === 'SelectedMonitors' ? selectedMonitorIds! : [] }, expectedRevision: null
    })}>Save connection</Button></div>
  </section>;
}

export function parseSelectedMonitorIds(value: string): number[] | null {
  const tokens = value.split(',').map(token => token.trim());
  if (tokens.length === 0 || tokens.some(token => !/^\d+$/.test(token))) return null;
  const ids = tokens.map(Number);
  if (ids.some(id => !Number.isSafeInteger(id) || id < 0 || id > 0xFFFFFFFF) || new Set(ids).size !== ids.length) return null;
  return ids;
}

function Productivity({ organization, workspace, onError }: { organization: Organization; workspace: Workspace; onError: (error: string) => void }) {
  const base = `/api/v1/organizations/${organization.id}/workspaces/${workspace.id}`;
  const [connections, setConnections] = useState<Connection[]>([]); const [folders, setFolders] = useState<Folder[]>([]); const [groups, setGroups] = useState<DynamicGroup[]>([]); const [templates, setTemplates] = useState<ConnectionTemplate[]>([]);
  const [state, setState] = useState<UserWorkspaceState | null>(null); const [selectedIds, setSelectedIds] = useState<string[]>([]); const [groupName, setGroupName] = useState(''); const [groupField, setGroupField] = useState('tag'); const [groupValue, setGroupValue] = useState('');
  const [templateName, setTemplateName] = useState(''); const [templateSourceId, setTemplateSourceId] = useState(''); const [savedName, setSavedName] = useState(''); const [tabGroupName, setTabGroupName] = useState(''); const [bulkTag, setBulkTag] = useState('');
  const [gatewayHost, setGatewayHost] = useState(''); const [gatewayResult, setGatewayResult] = useState(''); const [bulkPreview, setBulkPreview] = useState('');
  const reload = useCallback(async () => { try { const [connectionPage, folderItems, dynamicItems, templateItems, workspaceState] = await Promise.all([
    api.get<{ items: Connection[] }>(`${base}/connections?pageSize=200`), api.get<Folder[]>(`${base}/folders`), api.get<DynamicGroup[]>(`${base}/dynamic-groups`), api.get<ConnectionTemplate[]>(`${base}/connection-templates`), api.get<UserWorkspaceState>(`${base}/me/workspace-state`)
  ]); setConnections(connectionPage.items); setFolders(folderItems); setGroups(dynamicItems); setTemplates(templateItems); setState(workspaceState); setTemplateSourceId(current => current || connectionPage.items[0]?.id || ''); } catch (caught) { onError(message(caught)); } }, [base, onError]);
  useEffect(() => { void reload(); }, [reload]);
  const toggleConnection = (id: string) => setSelectedIds(current => current.includes(id) ? current.filter(item => item !== id) : [...current, id]);
  const savePersonalState = async () => { if (!state) return; try { const savedWorkspaces = savedName.trim() ? [...state.savedWorkspaces, { id: crypto.randomUUID(), name: savedName.trim(), connectionIds: selectedIds, displayMode: 'Embedded' as const }] : state.savedWorkspaces;
    const tabGroups = tabGroupName.trim() ? [...state.tabGroups, { id: crypto.randomUUID(), name: tabGroupName.trim(), color: '#5B8DEF', connectionIds: selectedIds }] : state.tabGroups;
    const saved = await api.putOptional<UserWorkspaceState>(`${base}/me/workspace-state`, { pinnedFolderIds: state.pinnedFolderIds, layout: state.layout, savedWorkspaces, tabGroups, expectedRevision: null }, state.revision); setState(saved); setSavedName(''); setTabGroupName('');
  } catch (caught) { onError(message(caught)); } };
  return <Page title="Workspace productivity" description="Personal layout state synchronizes separately from shared inventory. Bulk actions are previewed and bounded before any desktop launch.">
    <div className="two-column"><Card><h2>Dynamic group</h2><Field label="Name"><Input value={groupName} onChange={(_, data) => setGroupName(data.value)} /></Field><Field label="Metadata field"><Select value={groupField} onChange={event => setGroupField(event.target.value)}><option value="tag">Tag</option><option value="environment">Environment</option><option value="businessUnit">Business unit</option><option value="applicationOwner">Application owner</option><option value="serviceOwner">Service owner</option><option value="criticality">Criticality</option><option value="geography">Geography</option><option value="dataClassification">Data classification</option><option value="maintenanceWindow">Maintenance window</option><option value="supportGroup">Support group</option><option value="escalationContact">Escalation contact</option><option value="assetId">Asset ID</option><option value="cmdbId">CMDB ID</option><option value="operatingSystem">Operating system</option><option value="monitoringUrl">Monitoring URL</option><option value="documentationUrl">Documentation URL</option><option value="ticketingUrl">Ticketing URL</option></Select></Field><Field label="Equals"><Input value={groupValue} onChange={(_, data) => setGroupValue(data.value)} /></Field>
      <Button appearance="primary" disabled={!groupName.trim() || !groupValue.trim()} onClick={async () => { try { await api.post(`${base}/dynamic-groups`, { name: groupName.trim(), rules: [{ field: groupField, operator: 'equals', value: groupValue.trim() }], expectedRevision: null }); setGroupName(''); setGroupValue(''); await reload(); } catch (caught) { onError(message(caught)); } }}>Create group</Button>
      {groups.map(group => <div className="inline-row" key={group.id}><span><strong>{group.name}</strong> · {group.matchingConnectionIds.length} matches</span><Button appearance="subtle" onClick={async () => { try { await api.delete(`${base}/dynamic-groups/${group.id}`, group.revision); await reload(); } catch (caught) { onError(message(caught)); } }}>Delete</Button></div>)}</Card>
      <Card><h2>Connection template and safe copy</h2><Field label="Template name"><Input value={templateName} onChange={(_, data) => setTemplateName(data.value)} /></Field><Field label="Copy settings from"><Select value={templateSourceId} onChange={event => setTemplateSourceId(event.target.value)}>{connections.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</Select></Field>
      <Button appearance="primary" disabled={!templateName.trim() || !templateSourceId} onClick={async () => { const source = connections.find(item => item.id === templateSourceId); if (!source) return; try { await api.post(`${base}/connection-templates`, { name: templateName.trim(), description: `Safe defaults from ${source.name}`, defaults: { environment: source.environment, notes: source.notes, tags: source.tags, metadata: source.metadata, settings: source.settings }, expectedRevision: null }); setTemplateName(''); await reload(); } catch (caught) { onError(message(caught)); } }}>Create template</Button>
      <p className="hint">{templates.length} reusable templates. Targets, ownership, credentials, and audit history are never copied into a template.</p>{templateSourceId && <Button onClick={async () => { const source = connections.find(item => item.id === templateSourceId); if (!source) return; try { await api.post(`${base}/connections/${source.id}/copy`, { name: `${source.name} copy`, folderId: source.folderId, visibility: 'Personal' }); await reload(); } catch (caught) { onError(message(caught)); } }}>Create safe personal copy</Button>}</Card></div>
    <div className="two-column"><Card><h2>Pinned folders and personal layout</h2>{state && <><div className="check-list">{folders.map(folder => <Checkbox key={folder.id} label={folder.name} checked={state.pinnedFolderIds.includes(folder.id)} onChange={(_, data) => setState({ ...state, pinnedFolderIds: data.checked ? [...state.pinnedFolderIds, folder.id] : state.pinnedFolderIds.filter(id => id !== folder.id) })} />)}</div>
      <Field label="Saved workspace name"><Input value={savedName} onChange={(_, data) => setSavedName(data.value)} /></Field><Field label="Tab group name"><Input value={tabGroupName} onChange={(_, data) => setTabGroupName(data.value)} /></Field><Checkbox label="Reopen last workspace as disconnected descriptors" checked={state.layout.reopenLastWorkspace} onChange={(_, data) => setState({ ...state, layout: { ...state.layout, reopenLastWorkspace: data.checked === true } })} /><Button appearance="primary" onClick={() => void savePersonalState()}>Save personal workspace state</Button></>}</Card>
      <Card><h2>Bulk selection</h2><div className="check-list">{connections.map(connection => <Checkbox key={connection.id} label={`${connection.name} (${connection.environment})`} checked={selectedIds.includes(connection.id)} onChange={() => toggleConnection(connection.id)} />)}</div><Button disabled={selectedIds.length === 0} onClick={async () => { try { const result = await api.post<{ allowedConnectionIds: string[]; deniedConnectionIds: string[]; maximumConnections: number; warnings: string[] }>(`${base}/bulk/connect-preview`, { connectionIds: selectedIds }); setBulkPreview(`${result.allowedConnectionIds.length} allowed, ${result.deniedConnectionIds.length} denied. ${result.warnings.join(' ')}`); } catch (caught) { onError(message(caught)); } }}>Preview bulk connect</Button>{bulkPreview && <MessageBar intent="warning"><MessageBarBody>{bulkPreview}</MessageBarBody></MessageBar>}
      <Field label="Add tag to selection"><Input value={bulkTag} onChange={(_, data) => setBulkTag(data.value)} /></Field><Button disabled={!bulkTag.trim() || selectedIds.length === 0} onClick={async () => { try { await api.postIdempotent(`${base}/bulk/edit-connections`, { connectionIds: selectedIds, environment: null, folderId: null, clearFolder: false, addTags: [bulkTag.trim()], removeTags: [], applicationOwner: null, criticality: null }, crypto.randomUUID()); setBulkTag(''); await reload(); } catch (caught) { onError(message(caught)); } }}>Apply bounded bulk edit</Button></Card></div>
    <Card><h2>Gateway diagnostics</h2><div className="toolbar"><Input aria-label="Gateway host" placeholder="gateway.example.com" value={gatewayHost} onChange={(_, data) => setGatewayHost(data.value)} /><Button disabled={!gatewayHost.trim()} onClick={async () => { try { const result = await api.post<{ outcome: string; durationMilliseconds: number }>(`${base}/gateway/diagnostics`, { host: gatewayHost.trim(), port: 443, requireTls: true, expectedServerName: gatewayHost.trim() }); setGatewayResult(`${result.outcome} in ${result.durationMilliseconds} ms`); } catch (caught) { onError(message(caught)); } }}>Test DNS, TCP and TLS</Button><span>{gatewayResult}</span></div></Card>
  </Page>;
}

function Configuration({ organization, workspace, onError }: { organization: Organization; workspace: Workspace; onError: (error: string) => void }) {
  const [values, setValues] = useState('{\n  "client.minimumVersion": "0.2.0",\n  "client.bulkConnectLimit": 10,\n  "redirection.clipboard": "Bidirectional",\n  "rdp.requireNla": true\n}');
  const [locks, setLocks] = useState('rdp.requireNla'); const [summary, setSummary] = useState('Workspace baseline'); const [draft, setDraft] = useState<{ id: string; revision: number } | null>(null);
  const [history, setHistory] = useState<{ id: string; version: number; summary: string; values: Record<string, unknown>; lockedFields: string[]; publishedAtUtc: string }[]>([]); const [effective, setEffective] = useState<{ revision: number; fields: Record<string, { jsonValue: string; source: string; locked: boolean }>; conflicts: string[] } | null>(null);
  const [comparison, setComparison] = useState('');
  const base = `/api/v1/organizations/${organization.id}`;
  const reload = useCallback(async () => { try {
    const [historyItems, policy] = await Promise.all([
      api.get<typeof history>(`${base}/configuration/history?workspaceId=${workspace.id}&scopeType=Workspace&scopeId=${workspace.id}`),
      api.get<typeof effective>(`${base}/workspaces/${workspace.id}/configuration/effective`)
    ]); setHistory(historyItems); setEffective(policy);
  } catch (caught) { onError(message(caught)); } }, [base, onError, workspace.id]);
  useEffect(() => { void reload(); }, [reload]);
  const saveDraft = async () => { try { const parsed = JSON.parse(values) as Record<string, unknown>; const request = { scopeType: 'Workspace', scopeId: workspace.id, baseVersion: history[0]?.version ?? 0, values: parsed, lockedFields: locks.split(',').map(value => value.trim()).filter(Boolean), summary, expectedRevision: null };
    const saved = draft ? await api.put<{ id: string; revision: number }>(`${base}/configuration/drafts/${draft.id}?workspaceId=${workspace.id}`, request, draft.revision) : await api.post<{ id: string; revision: number }>(`${base}/configuration/drafts?workspaceId=${workspace.id}`, request); setDraft(saved);
  } catch (caught) { onError(message(caught)); } };
  return <Page title="Configuration policy" description="Organization → workspace → folder → connection precedence, with explicit provenance and locks." actions={<Button appearance="primary" onClick={() => void saveDraft()}>Save draft</Button>}>
    <div className="two-column"><Card><h2>Workspace draft</h2><Field label="Summary"><Input value={summary} onChange={(_, data) => setSummary(data.value)} /></Field><Field label="Locked fields" hint="Comma separated; locked values cannot be overridden below this scope"><Input value={locks} onChange={(_, data) => setLocks(data.value)} /></Field><Field label="Policy values (JSON)" hint="Validated against the Phase 2 RDP policy schema before saving"><Textarea className="code-editor" resize="vertical" value={values} onChange={(_, data) => setValues(data.value)} /></Field>
      <details><summary>Supported policy fields and safe defaults</summary><p className="hint">Use rdp.requireNla, administrative session, gateway host/usage/port; display mode, dimensions and color depth; performance preset; thumbnail privacy and shortcut routing; microphone, camera, COM/LPT, USB, smart-card, printer, drive, audio, clipboard and file-transfer redirection; session timeout, reconnect attempts, minimum client version and bulk-connect limit. Unknown or incorrectly typed fields are rejected.</p></details>
      <div className="editor-actions"><Button onClick={() => void saveDraft()}>Save draft</Button><Button appearance="primary" disabled={!draft} onClick={async () => { try { await api.post(`${base}/configuration/drafts/${draft!.id}/publish?workspaceId=${workspace.id}`); setDraft(null); await reload(); } catch (caught) { onError(message(caught)); } }}>Publish</Button></div></Card>
      <Card><h2>Effective policy</h2>{!effective || Object.keys(effective.fields).length === 0 ? <Empty title="No published policy" detail="Publish the first workspace baseline to establish central settings." /> : <><p>Effective revision {effective.revision}</p><dl className="policy-list">{Object.entries(effective.fields).map(([name, field]) => <div key={name}><dt>{name} {field.locked && <Badge color="warning">Locked</Badge>}</dt><dd>{field.jsonValue}<small>From {field.source}</small></dd></div>)}</dl>{effective.conflicts.length > 0 && <MessageBar intent="warning"><MessageBarBody>{effective.conflicts.join(' ')}</MessageBarBody></MessageBar>}</>}</Card>
    </div><h2>Published history</h2>{comparison && <Card><h3>Version comparison</h3><pre>{comparison}</pre><Button onClick={() => setComparison('')}>Close comparison</Button></Card>}{history.length === 0 ? <Empty title="No published versions" detail="Draft and publish a baseline above." /> : <div className="table-wrap"><table><thead><tr><th>Version</th><th>Summary</th><th>Published</th><th>Action</th></tr></thead><tbody>{history.map((item, index) => <tr key={item.id}><td>v{item.version}</td><td>{item.summary}</td><td>{formatDate(item.publishedAtUtc)}</td><td><Button appearance="subtle" onClick={() => { const previous = history[index + 1]; setComparison(`v${item.version}\n${JSON.stringify(item.values, null, 2)}\nLocks: ${item.lockedFields.join(', ') || 'None'}${previous ? `\n\nPrevious v${previous.version}\n${JSON.stringify(previous.values, null, 2)}\nLocks: ${previous.lockedFields.join(', ') || 'None'}` : '\n\nNo earlier version.'}`); }}>Compare</Button><Button appearance="subtle" onClick={async () => { try { await api.post(`${base}/configuration/published/${item.id}/rollback?workspaceId=${workspace.id}`, { summary: `Rollback to version ${item.version}` }); await reload(); } catch (caught) { onError(message(caught)); } }}>Roll back</Button></td></tr>)}</tbody></table></div>}
  </Page>;
}

function Access({ organization, workspaces, onError }: { organization: Organization; workspaces: Workspace[]; onError: (error: string) => void }) {
  const [users, setUsers] = useState<User[]>([]); const [memberships, setMemberships] = useState<Membership[]>([]); const [accounts, setAccounts] = useState<ServiceAccount[]>([]); const [mappings, setMappings] = useState<GroupRoleMapping[]>([]);
  const [userId, setUserId] = useState(''); const [workspaceId, setWorkspaceId] = useState(''); const [role, setRole] = useState('Operator'); const [accountName, setAccountName] = useState(''); const [secret, setSecret] = useState<{ clientId: string; clientSecret: string } | null>(null);
  const [groupClaim, setGroupClaim] = useState(''); const [groupRole, setGroupRole] = useState('Operator'); const [groupWorkspaceId, setGroupWorkspaceId] = useState('');
  const base = `/api/v1/organizations/${organization.id}`;
  const reload = useCallback(async () => { try { const [userItems, membershipItems, accountItems, mappingItems] = await Promise.all([api.get<User[]>(`${base}/users`), api.get<Membership[]>(`${base}/memberships`), api.get<ServiceAccount[]>(`${base}/service-accounts`), api.get<GroupRoleMapping[]>(`${base}/group-role-mappings`)]); setUsers(userItems); setMemberships(membershipItems); setAccounts(accountItems); setMappings(mappingItems); setUserId(current => current || userItems[0]?.id || ''); } catch (caught) { onError(message(caught)); } }, [base, onError]);
  useEffect(() => { void reload(); }, [reload]);
  return <Page title="Access management" description="Fixed Phase 2 roles, scoped service accounts, revocation, and explainable organization/workspace boundaries.">
    {secret && <MessageBar intent="warning"><MessageBarBody><strong>Copy this secret now. It will not be shown again.</strong><code>{secret.clientId}</code><code>{secret.clientSecret}</code></MessageBarBody><Button onClick={() => setSecret(null)}>I copied it</Button></MessageBar>}
    <div className="two-column"><Card><h2>Assign role</h2><Field label="User"><Select value={userId} onChange={event => setUserId(event.target.value)}>{users.map(user => <option key={user.id} value={user.id}>{user.displayName} {user.emailHint && `(${user.emailHint})`}</option>)}</Select></Field>
      <Field label="Scope"><Select value={workspaceId} onChange={event => setWorkspaceId(event.target.value)}><option value="">Organization</option>{workspaces.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</Select></Field>
      <Field label="Role"><Select value={role} onChange={event => setRole(event.target.value)}><option>OrganizationAdministrator</option><option>WorkspaceAdministrator</option><option>Operator</option><option>Auditor</option></Select></Field>
      <Button appearance="primary" disabled={!userId || ((role === 'WorkspaceAdministrator' || role === 'Operator') && !workspaceId)} onClick={async () => { try { await api.post(`${base}/memberships`, { userId, workspaceId: workspaceId || null, role, status: 'Active', sourceGroup: '', expectedRevision: null }); await reload(); } catch (caught) { onError(message(caught)); } }}>Assign role</Button></Card>
      <Card><h2>Create service account</h2><Field label="Name"><Input value={accountName} onChange={(_, data) => setAccountName(data.value)} /></Field><Field label="Workspace (optional)"><Select value={workspaceId} onChange={event => setWorkspaceId(event.target.value)}><option value="">Organization scope</option>{workspaces.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</Select></Field>
      <p className="hint">Default scopes: inventory.read, inventory.write, configuration.read, bulk.write.</p><Button appearance="primary" disabled={!accountName} onClick={async () => { try { const created = await api.post<{ clientId: string; clientSecret: string }>(`${base}/service-accounts`, { name: accountName, workspaceId: workspaceId || null, scopes: ['inventory.read', 'inventory.write', 'configuration.read', 'bulk.write'], expiresAtUtc: null }); setSecret(created); setAccountName(''); await reload(); } catch (caught) { onError(message(caught)); } }}>Create account</Button></Card></div>
    <Card className="form-card"><h2>Map identity-provider group to role</h2><Field label="Exact group claim"><Input value={groupClaim} onChange={(_, data) => setGroupClaim(data.value)} /></Field><Field label="Scope"><Select value={groupWorkspaceId} onChange={event => setGroupWorkspaceId(event.target.value)}><option value="">Organization</option>{workspaces.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</Select></Field><Field label="Role"><Select value={groupRole} onChange={event => setGroupRole(event.target.value)}><option>OrganizationAdministrator</option><option>WorkspaceAdministrator</option><option>Operator</option><option>Auditor</option></Select></Field><Button appearance="primary" disabled={!groupClaim.trim() || ((groupRole === 'WorkspaceAdministrator' || groupRole === 'Operator') && !groupWorkspaceId)} onClick={async () => { try { await api.post(`${base}/group-role-mappings`, { workspaceId: groupWorkspaceId || null, groupClaim: groupClaim.trim(), role: groupRole, enabled: true, expectedRevision: null }); setGroupClaim(''); await reload(); } catch (caught) { onError(message(caught)); } }}>Create mapping</Button></Card>
    <h2>Group-role mappings</h2>{mappings.length === 0 ? <Empty title="No group mappings" detail="Group claims have no persisted role mapping in this organization." /> : <div className="table-wrap"><table><thead><tr><th>Group</th><th>Scope</th><th>Role</th><th>Status</th><th /></tr></thead><tbody>{mappings.map(item => <tr key={item.id}><td>{item.groupClaim}</td><td>{workspaces.find(workspace => workspace.id === item.workspaceId)?.name ?? 'Organization'}</td><td>{item.role}</td><td>{item.enabled ? 'Enabled' : 'Disabled'}</td><td><Button appearance="subtle" onClick={async () => { try { await api.delete(`${base}/group-role-mappings/${item.id}`, item.revision); await reload(); } catch (caught) { onError(message(caught)); } }}>Delete</Button></td></tr>)}</tbody></table></div>}
    <h2>Memberships</h2>{memberships.length === 0 ? <Empty title="No delegated memberships" detail="The platform administrator can assign the first role above." /> : <div className="table-wrap"><table><thead><tr><th>User</th><th>Scope</th><th>Role</th><th>Status</th><th /></tr></thead><tbody>{memberships.map(item => <tr key={item.id}><td>{users.find(user => user.id === item.userId)?.displayName ?? item.userId}</td><td>{workspaces.find(workspace => workspace.id === item.workspaceId)?.name ?? 'Organization'}</td><td>{item.role}</td><td>{item.status}</td><td><Button appearance="subtle" disabled={item.status === 'Revoked'} onClick={async () => { try { await api.delete(`${base}/memberships/${item.id}`, item.revision); await reload(); } catch (caught) { onError(message(caught)); } }}>Revoke</Button></td></tr>)}</tbody></table></div>}
    <h2>Service accounts</h2>{accounts.length === 0 ? <Empty title="No service accounts" detail="Create a scoped automation identity above." /> : <div className="table-wrap"><table><thead><tr><th>Name</th><th>Client ID</th><th>Scopes</th><th>Status</th><th /></tr></thead><tbody>{accounts.map(item => <tr key={item.id}><td>{item.name}</td><td><code>{item.clientId}</code></td><td>{item.scopes.join(', ')}</td><td>{item.revokedAtUtc ? 'Revoked' : 'Active'}</td><td><Button appearance="subtle" disabled={!!item.revokedAtUtc} onClick={async () => { try { const rotated = await api.post<{ clientId: string; clientSecret: string }>(`${base}/service-accounts/${item.id}/rotate-secret`); setSecret(rotated); } catch (caught) { onError(message(caught)); } }}>Rotate</Button><Button appearance="subtle" disabled={!!item.revokedAtUtc} onClick={async () => { try { await api.delete(`${base}/service-accounts/${item.id}`); await reload(); } catch (caught) { onError(message(caught)); } }}>Revoke</Button></td></tr>)}</tbody></table></div>}
  </Page>;
}

function Audit({ organization, workspace, onError }: { organization: Organization; workspace?: Workspace; onError: (error: string) => void }) {
  const [events, setEvents] = useState<AuditEvent[]>([]); const [loading, setLoading] = useState(true);
  useEffect(() => { const query = workspace ? `?workspaceId=${workspace.id}&limit=500` : '?limit=500'; void api.get<AuditEvent[]>(`/api/v1/organizations/${organization.id}/audit${query}`).then(setEvents).catch(caught => onError(message(caught))).finally(() => setLoading(false)); }, [onError, organization.id, workspace]);
  return <Page title="Audit activity" description="Append-only, redacted administrative events. Credentials and secrets are structurally excluded.">{loading ? <Spinner label="Loading audit events" /> : events.length === 0 ? <Empty title="No audit events" detail="Authorized changes will appear here." /> : <div className="table-wrap"><table><thead><tr><th>Time</th><th>Actor</th><th>Action</th><th>Resource</th><th>Outcome</th><th>Correlation</th></tr></thead><tbody>{events.map(item => <tr key={item.id}><td>{formatDate(item.occurredAtUtc)}</td><td>{item.actorAlias}</td><td>{item.action}</td><td>{item.resourceType}</td><td><Badge color={item.outcome === 'Succeeded' ? 'success' : 'danger'}>{item.outcome}</Badge></td><td><code>{item.correlationId}</code></td></tr>)}</tbody></table></div>}</Page>;
}

function SystemHealth({ onError }: { onError: (error: string) => void }) {
  const [health, setHealth] = useState<Health | null>(null); const refresh = useCallback(async () => { try { setHealth(await api.get<Health>('/health/ready')); } catch (caught) { onError(message(caught)); } }, [onError]);
  useEffect(() => { void refresh(); const timer = window.setInterval(() => void refresh(), 15_000); return () => window.clearInterval(timer); }, [refresh]);
  return <Page title="System health" description="Live dependency readiness with no database, policy, or network secrets in responses." actions={<Button onClick={() => void refresh()}>Refresh</Button>}>
    {!health ? <Spinner label="Checking dependencies" /> : <><MessageBar intent={health.status === 'healthy' ? 'success' : 'error'}><MessageBarBody>Control plane is {health.status}. Probe completed in {health.durationMs} ms.</MessageBarBody></MessageBar><div className="metric-grid">{health.checks.map(check => <Metric key={check.name} label={check.name} value={`${check.status} · ${check.durationMs} ms`} />)}</div></>}
    <Card><h2>Operational endpoints</h2><dl><dt>OpenAPI 3.1</dt><dd><a href="/openapi/v1.json" target="_blank" rel="noreferrer">/openapi/v1.json</a></dd><dt>Liveness</dt><dd>/health/live</dd><dt>Readiness</dt><dd>/health/ready</dd></dl></Card>
  </Page>;
}

function CommandPalette({ current, onChoose, onClose }: { current: View; onChoose: (view: View) => void; onClose: () => void }) {
  const [filter, setFilter] = useState(''); const input = useRef<HTMLInputElement>(null); useEffect(() => input.current?.focus(), []);
  const filtered = useMemo(() => views.filter(item => `${item.label} ${item.description}`.toLowerCase().includes(filter.toLowerCase())), [filter]);
  return <div className="palette-backdrop" onMouseDown={event => { if (event.target === event.currentTarget) onClose(); }}><section className="palette" role="dialog" aria-modal="true" aria-label="Command palette"><Input ref={input} aria-label="Filter commands" placeholder="Type a command or destination" value={filter} onChange={(_, data) => setFilter(data.value)} />
    <div role="listbox" aria-label="Commands">{filtered.map(item => <button key={item.id} role="option" aria-selected={item.id === current} onClick={() => onChoose(item.id)}><strong>Open {item.label}</strong><span>{item.description}</span></button>)}</div><Button appearance="subtle" onClick={onClose}>Close</Button></section></div>;
}

function Page({ title, description, actions, children }: { title: string; description: string; actions?: React.ReactNode; children: React.ReactNode }) { return <><div className="page-heading"><div><h1>{title}</h1><p>{description}</p></div>{actions}</div>{children}</>; }
function Metric({ label, value }: { label: string; value: string }) { return <Card className="metric"><span>{label}</span><strong>{value}</strong></Card>; }
function Empty({ title, detail }: { title: string; detail: string }) { return <div className="empty"><strong>{title}</strong><span>{detail}</span></div>; }
function Centered({ children }: { children: React.ReactNode }) { return <main className="centered">{children}</main>; }
function formatDate(value: string) { return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value)); }
function slugify(value: string) { return value.toLowerCase().trim().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 63); }
function message(error: unknown) { return error instanceof Error ? error.message : 'An unexpected request error occurred.'; }
