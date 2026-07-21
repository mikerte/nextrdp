import { render, screen } from '@testing-library/react';
import { FluentProvider, webDarkTheme } from '@fluentui/react-components';
import { App, parseSelectedMonitorIds } from './App';

afterEach(() => vi.restoreAllMocks());

it('offers corporate sign-in when no authenticated session exists', async () => {
  vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({ code: 'access_denied', detail: 'Authentication is required.' }), {
    status: 401, headers: { 'Content-Type': 'application/problem+json' }
  }));
  render(<FluentProvider theme={webDarkTheme}><App /></FluentProvider>);
  expect(await screen.findByRole('heading', { name: 'Sign in to NextRDP' })).toBeInTheDocument();
  expect(screen.getByRole('link', { name: 'Continue with corporate sign-in' })).toHaveAttribute('href', '/auth/login?returnUrl=/');
});

it('renders keyboard-accessible navigation for an authenticated administrator', async () => {
  const responses = [
    { principalId: '00000000-0000-0000-0000-000000000001', organizationId: null, roles: ['PlatformAdministrator'], groups: [], mfaSatisfied: true, isServiceAccount: false },
    [{ id: 'org', name: 'Example Corp', slug: 'example', revision: 1, updatedAtUtc: '2026-07-19T00:00:00Z' }],
    [{ id: 'workspace', organizationId: 'org', name: 'Operations', slug: 'operations', description: 'Managed servers', revision: 1, updatedAtUtc: '2026-07-19T00:00:00Z' }]
  ];
  vi.spyOn(globalThis, 'fetch').mockImplementation(async () => new Response(JSON.stringify(responses.shift()), { status: 200, headers: { 'Content-Type': 'application/json' } }));
  render(<FluentProvider theme={webDarkTheme}><App /></FluentProvider>);
  expect(await screen.findByRole('navigation', { name: 'Administration navigation' })).toBeInTheDocument();
  expect(screen.getByRole('button', { name: /Inventory/ })).toBeInTheDocument();
  expect(screen.getByRole('button', { name: /Commands/ })).toHaveAttribute('aria-keyshortcuts', 'Control+K');
});

it('parses only bounded unique monitor identifiers', () => {
  expect(parseSelectedMonitorIds('0, 2, 1')).toEqual([0, 2, 1]);
  expect(parseSelectedMonitorIds('')).toBeNull();
  expect(parseSelectedMonitorIds('0, 0')).toBeNull();
  expect(parseSelectedMonitorIds('-1')).toBeNull();
  expect(parseSelectedMonitorIds('4294967296')).toBeNull();
  expect(parseSelectedMonitorIds('primary')).toBeNull();
});
