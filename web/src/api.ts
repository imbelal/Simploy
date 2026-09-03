// Empty => relative /api/... (proxied by nginx in the web container, so it works
// from any host with no CORS issues). Set VITE_API_URL only for local `npm run dev`.
import { trackLoading, untrackLoading } from './loading'

const API = import.meta.env.VITE_API_URL ?? ''
const TOKEN_KEY = 'simploy.jwt'

export const auth = {
  token: () => localStorage.getItem(TOKEN_KEY),
  isLoggedIn: () => !!localStorage.getItem(TOKEN_KEY),
  async login(username: string, password: string) {
    const r = await req<{ token: string; username: string }>('/api/auth/login', { method: 'POST', body: JSON.stringify({ username, password }) }, true)
    localStorage.setItem(TOKEN_KEY, r.token)
    return r
  },
  logout() { localStorage.removeItem(TOKEN_KEY); window.location.href = '/login' },
}

async function req<T>(path: string, opts?: RequestInit, isLogin = false): Promise<T> {
  const token = localStorage.getItem(TOKEN_KEY)
  trackLoading()
  try {
    const res = await fetch(`${API}${path}`, {
      headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
      ...opts,
    })
    if (res.status === 401 && !isLogin) {
      localStorage.removeItem(TOKEN_KEY)
      window.location.href = '/login'
      throw new Error('Session expired')
    }
    if (!res.ok) throw new Error(`${res.status} ${await res.text()}`)
    return res.status === 204 ? null as T : res.json()
  } finally {
    untrackLoading()
  }
}

export const api = {
  github: {
    install: () => req<any>('/api/github/install'),
    installations: () => req<any[]>('/api/github/installations'),
    repositories: (installationId: string) => req<any[]>(`/api/github/repositories?installationId=${encodeURIComponent(installationId)}`),
    bindProject: (projectId: string, installationId: string) => req<any>(`/api/github/projects/${projectId}/installation`, { method: 'PUT', body: JSON.stringify({ installationId }) }),
  },
  servers: {
    list: () => req<any[]>('/api/servers'),
    create: (b: any) => req<any>('/api/servers', { method: 'POST', body: JSON.stringify(b) }),
    check: (id: string) => req<any>(`/api/servers/${id}/check`, { method: 'POST' }),
    del: (id: string) => req<any>(`/api/servers/${id}`, { method: 'DELETE' }),
    containers: (id: string) => req<any[]>(`/api/servers/${id}/containers`),
    restartContainers: () => req<any>('/api/servers/containers/restart', { method: 'POST' }),
  },
  projects: {
    list: () => req<any[]>('/api/projects'),
    create: (b: any) => req<any>('/api/projects', { method: 'POST', body: JSON.stringify(b) }),
    del: (id: string) => req<any>(`/api/projects/${id}`, { method: 'DELETE' }),
    branches: (id: string) => req<any>(`/api/github/projects/${id}/branches`),
    detect: (repo: string, installationId?: string) => req<any>(`/api/github/detect?repo=${encodeURIComponent(repo)}${installationId ? `&installationId=${encodeURIComponent(installationId)}` : ''}`),
  },
  envs: {
    list: (projectId?: string) => req<any[]>(`/api/environments${projectId ? `?projectId=${projectId}` : ''}`),
    create: (b: any) => req<any>('/api/environments', { method: 'POST', body: JSON.stringify(b) }),
    del: (id: string) => req<any>(`/api/environments/${id}`, { method: 'DELETE' }),
    setEnv: (id: string, envVars: Record<string, string>) => req<any>(`/api/environments/${id}/env-vars`, { method: 'PUT', body: JSON.stringify({ envVars }) }),
    setDomains: (id: string, domains: any[]) => req<any>(`/api/environments/${id}/domains`, { method: 'PUT', body: JSON.stringify({ domains }) }),
  },
  deployments: {
    list: (envId?: string) => req<any[]>(`/api/deployments${envId ? `?environmentId=${envId}` : ''}`),
    create: (b: any) => req<any>('/api/deployments', { method: 'POST', body: JSON.stringify(b) }),
    runAll: () => req<any>('/api/deployments/run-all', { method: 'POST' }),
  },
  databases: {
    list: () => req<any[]>('/api/databases'),
    create: (b: any) => req<any>('/api/databases', { method: 'POST', body: JSON.stringify(b) }),
    del: (id: string) => req<any>(`/api/databases/${id}`, { method: 'DELETE' }),
    runAll: () => req<any>('/api/databases/run-all', { method: 'POST' }),
  },
  backups: {
    get: () => req<any>('/api/settings/backups'),
    set: (b: any) => req<any>('/api/settings/backups', { method: 'PUT', body: JSON.stringify(b) }),
    run: () => req<any>('/api/settings/backups/run', { method: 'POST' }),
    list: () => req<any[]>('/api/settings/backups/list'),
    restore: (file: string) => req<any>('/api/settings/backups/restore', { method: 'POST', body: JSON.stringify({ file }) }),
  },
};
