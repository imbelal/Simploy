// Empty => relative /api/... (proxied by nginx in the web container, so it works
// from any host with no CORS issues). Set VITE_API_URL only for local `npm run dev`.
const API = import.meta.env.VITE_API_URL ?? ''

async function req<T>(path: string, opts?: RequestInit): Promise<T> {
  const res = await fetch(`${API}${path}`, { headers: { 'Content-Type': 'application/json' }, ...opts });
  if (!res.ok) throw new Error(`${res.status} ${await res.text()}`);
  return res.status === 204 ? null as T : res.json();
}

export const api = {
  servers: {
    list: () => req<any[]>('/api/servers'),
    create: (b: any) => req<any>('/api/servers', { method: 'POST', body: JSON.stringify(b) }),
    check: (id: string) => req<any>(`/api/servers/${id}/check`, { method: 'POST' }),
    del: (id: string) => req<any>(`/api/servers/${id}`, { method: 'DELETE' }),
  },
  projects: {
    list: () => req<any[]>('/api/projects'),
    create: (b: any) => req<any>('/api/projects', { method: 'POST', body: JSON.stringify(b) }),
    del: (id: string) => req<any>(`/api/projects/${id}`, { method: 'DELETE' }),
  },
  envs: {
    list: (projectId?: string) => req<any[]>(`/api/environments${projectId ? `?projectId=${projectId}` : ''}`),
    create: (b: any) => req<any>('/api/environments', { method: 'POST', body: JSON.stringify(b) }),
    del: (id: string) => req<any>(`/api/environments/${id}`, { method: 'DELETE' }),
    setEnv: (id: string, envVars: Record<string, string>) => req<any>(`/api/environments/${id}/env-vars`, { method: 'PUT', body: JSON.stringify({ envVars }) }),
  },
  deployments: {
    list: (envId?: string) => req<any[]>(`/api/deployments${envId ? `?environmentId=${envId}` : ''}`),
    create: (b: any) => req<any>('/api/deployments', { method: 'POST', body: JSON.stringify(b) }),
  },
};
