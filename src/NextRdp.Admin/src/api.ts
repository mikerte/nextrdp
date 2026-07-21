export class ApiError extends Error {
  constructor(public readonly status: number, public readonly code: string, message: string, public readonly errors?: Record<string, string[]>) {
    super(message);
  }
}

class ApiClient {
  private csrfToken: string | null = null;

  async get<T>(path: string): Promise<T> { return this.request<T>(path); }
  async post<T>(path: string, body?: unknown): Promise<T> { return this.request<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) }); }
  async postIdempotent<T>(path: string, body: unknown, key: string): Promise<T> { return this.request<T>(path, { method: 'POST', body: JSON.stringify(body), headers: { 'Idempotency-Key': key } }); }
  async put<T>(path: string, body: unknown, revision: number): Promise<T> { return this.request<T>(path, { method: 'PUT', body: JSON.stringify(body), headers: { 'If-Match': `"${revision}"` } }); }
  async putOptional<T>(path: string, body: unknown, revision?: number): Promise<T> { return this.request<T>(path, { method: 'PUT', body: JSON.stringify(body), headers: revision && revision > 0 ? { 'If-Match': `"${revision}"` } : undefined }); }
  async delete(path: string, revision?: number): Promise<void> { await this.request<void>(path, { method: 'DELETE', headers: revision === undefined ? undefined : { 'If-Match': `"${revision}"` } }); }

  private async request<T>(path: string, init: RequestInit = {}): Promise<T> {
    const method = init.method ?? 'GET';
    const headers = new Headers(init.headers);
    headers.set('Accept', 'application/json');
    if (init.body) headers.set('Content-Type', 'application/json');
    if (method !== 'GET' && method !== 'HEAD') {
      if (!this.csrfToken) {
        const tokenResponse = await fetch('/auth/csrf', { credentials: 'same-origin', headers: { Accept: 'application/json' } });
        if (!tokenResponse.ok) await this.fail(tokenResponse);
        this.csrfToken = (await tokenResponse.json() as { requestToken: string }).requestToken;
      }
      headers.set('X-CSRF-TOKEN', this.csrfToken);
    }
    const response = await fetch(path, { ...init, headers, credentials: 'same-origin' });
    if (!response.ok) {
      if (response.status === 400 && this.csrfToken && method !== 'GET') this.csrfToken = null;
      await this.fail(response);
    }
    if (response.status === 204 || response.headers.get('content-length') === '0') return undefined as T;
    return await response.json() as T;
  }

  private async fail(response: Response): Promise<never> {
    let problem: { code?: string; detail?: string; title?: string; errors?: Record<string, string[]> } = {};
    try { problem = await response.json() as typeof problem; } catch { /* response is not JSON */ }
    throw new ApiError(response.status, problem.code ?? 'request_failed', problem.detail ?? problem.title ?? `Request failed (${response.status}).`, problem.errors);
  }
}

export const api = new ApiClient();
