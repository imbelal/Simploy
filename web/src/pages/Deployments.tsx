import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api'
import { Badge, Button, Card, EmptyState, Field, PageHeader, Panel, inputCls } from '../components/Field'

const statusInfo: Record<string, { label: string; tone: 'pending' | 'blue' | 'ok' | 'red' | 'slate' }> = {
  Queued: { label: 'Queued', tone: 'pending' },
  Building: { label: 'Building', tone: 'blue' },
  Deploying: { label: 'Deploying', tone: 'blue' },
  Healthy: { label: 'Healthy', tone: 'ok' },
  Failed: { label: 'Failed', tone: 'red' },
  RolledBack: { label: 'RolledBack', tone: 'slate' },
}

export default function Deployments() {
  const [projects, setProjects] = useState<any[]>([])
  const [envs, setEnvs] = useState<any[]>([])
  const [deps, setDeps] = useState<any[]>([])
  const [imageTag, setImageTag] = useState('prod-' + Math.random().toString(36).slice(2, 7))
  const [projectId, setProjectId] = useState('')
  const [envId, setEnvId] = useState('')
  const [msg, setMsg] = useState('')
  const projectRef = useRef('')
  const envRef = useRef('')

  // Load the environments of a project and keep the previous env if still valid,
  // otherwise auto-select the first one.
  const applyEnv = (list: any[]) => {
    const keep = envRef.current && list.some((e: any) => e.id === envRef.current)
    const id = keep ? envRef.current : list[0]?.id ?? ''
    envRef.current = id
    setEnvId(id)
  }
  const loadEnvs = async (pid: string) => {
    const e = await api.envs.list(pid || undefined).catch(() => [])
    setEnvs(e)
    applyEnv(e)
  }

  const load = async () => {
    const p = await api.projects.list().catch(() => [])
    setProjects(p)
    // Default to the first project once; never override a user selection.
    if (!projectRef.current && p[0]) { projectRef.current = p[0].id; setProjectId(p[0].id) }
    await loadEnvs(projectRef.current)
    setDeps(await api.deployments.list().catch(() => []))
  }
  useEffect(() => { load(); const i = setInterval(load, 3000); return () => clearInterval(i) }, [])

  const onProject = (pid: string) => {
    projectRef.current = pid
    setProjectId(pid)
    envRef.current = ''          // force a fresh selection for the new project
    loadEnvs(pid)
  }

  const projectEnvIds = new Set(envs.map((e: any) => e.id))
  const visibleDeps = projectId ? deps.filter((d: any) => projectEnvIds.has(d.environmentId)) : deps

  const deploy = async (strategy: string) => {
    if (!envId) return setMsg('Pick an environment first')
    setMsg(`Deploying ${imageTag} (${strategy})...`)
    try { await api.deployments.create({ environmentId: envId, imageTag, strategy }); setMsg('Queued — watch table below'); load() }
    catch (e: any) { setMsg(e.message) }
  }

  return (
    <div className="space-y-6">
      <PageHeader title="Deployments" desc="Trigger a build and see how it runs. Recreate swaps the app; Canary keeps the previous version running and shifts traffic over gradually." />

      <Panel>
        <div className="font-semibold text-slate-900 mb-4">Trigger a deploy</div>
        {projects.length === 0 ? (
          <div className="rounded-xl bg-amber-50 border border-amber-200 p-4 text-sm text-amber-900 leading-relaxed">
            You need a <span className="font-medium">project</span> first — it's your app (git repo + Dockerfile), linked to a <span className="font-medium">server</span> via an environment.<br />
            <Link to="/projects" className="mt-2 inline-block text-indigo-700 underline font-medium">Go to Projects → create a project and add an environment</Link>
          </div>
        ) : (
          <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
            <Field label="Project" hint="The app to deploy.">
              <select value={projectId} onChange={e => onProject(e.target.value)} className={inputCls}>
                {projects.map(p => <option key={p.id} value={p.id}>{p.name} ({p.slug})</option>)}
              </select>
            </Field>
            <Field label="Environment" hint="Which server + slot to deploy to.">
              <select value={envId} onChange={e => { envRef.current = e.target.value; setEnvId(e.target.value) }} className={inputCls}>
                {envs.map(e => <option key={e.id} value={e.id}>{e.name} ({e.slot}) on {e.server?.name} {e.server?.host}</option>)}
                {envs.length === 0 && <option value="">No environments in this project</option>}
              </select>
            </Field>
            <Field label="Image tag" hint="The tag for the built image, e.g. prod-abc123. Any label works.">
              <input value={imageTag} onChange={e => setImageTag(e.target.value)} className={inputCls + ' font-mono'} placeholder="prod-abc123" />
            </Field>
          </div>
        )}
        {envs.length > 0 && (
          <div className="flex gap-3 mt-4 flex-wrap">
            <Button variant="primary" onClick={() => deploy('Recreate')} title="Rebuild and swap out">Recreate</Button>
            <Button variant="secondary" onClick={() => deploy('Canary')} title="Deploy alongside the old version, shift traffic gradually">Canary</Button>
          </div>
        )}
        {msg && <div className="mt-3 text-xs bg-slate-50 border border-slate-200 rounded-lg p-2.5 font-mono text-slate-600">{msg}</div>}
        <div className="mt-3 text-xs text-slate-500 leading-relaxed">With an Agent on :8089 this <span className="text-slate-700 font-medium">clones your Git repo, builds the image, and runs <code className="bg-slate-100 px-1.5 rounded font-mono text-[11px]">docker compose up -d</code></span>. Without one the deploy ends <span className="text-red-600">Failed</span> — expected for the local demo.</div>
      </Panel>

      <Card title={`Recent deployments (${visibleDeps.length})`}>
        <table className="w-full text-sm">
          <thead className="bg-slate-50 text-slate-500"><tr><th className="text-left px-5 py-3 font-medium">When</th><th className="text-left px-4 py-3 font-medium">Environment</th><th className="text-left px-4 py-3 font-medium">Tag</th><th className="text-left px-4 py-3 font-medium">Status</th><th className="text-left px-4 py-3 font-medium">Reach app</th><th className="text-left px-4 py-3 font-medium">Error / Log</th></tr></thead>
          <tbody>
            {visibleDeps.map(d => {
              const label = typeof d.status === 'number' ? ['Queued', 'Building', 'Deploying', 'Healthy', 'Failed', 'RolledBack'][d.status] : d.status
              const info = statusInfo[label] || { label, tone: 'slate' as const }
              const healthy = label === 'Healthy'
              return (
                <tr key={d.id} className="border-t border-slate-100 hover:bg-slate-50/60 transition">
                  <td className="px-5 py-3 text-xs text-slate-500">{new Date(d.createdAt).toLocaleString()}</td>
                  <td className="px-4 py-3 text-xs"><div className="font-medium text-slate-800">{d.environmentName || d.environmentId.slice(0, 8)}</div>{d.serverName && <div className="text-slate-400 text-[11px]">{d.serverName}</div>}</td>
                  <td className="px-4 py-3 font-mono text-xs text-slate-600">{d.imageTag}</td>
                  <td className="px-4 py-3"><Badge tone={info.tone}>{info.label}</Badge></td>
                  <td className="px-4 py-3 text-xs">
                    {d.accessUrl ? (
                      healthy
                        ? <a href={d.accessUrl} target="_blank" rel="noreferrer" className="text-indigo-600 hover:underline font-medium font-mono">{d.accessUrl.replace(/^https?:\/\//, '')}</a>
                        : <span className="text-slate-400 font-mono" title="App still coming up — link is live once status is Healthy">{d.accessUrl.replace(/^https?:\/\//, '')}</span>
                    ) : <span className="text-slate-300">—</span>}
                  </td>
                  <td className="px-4 py-3 text-xs text-red-600 max-w-[220px] truncate" title={d.error || d.logOutput || ''}>{d.error || ''}</td>
                </tr>
              )
            })}
            {visibleDeps.length === 0 && <tr><td colSpan={6}><EmptyState title="No deployments yet">Trigger one above.</EmptyState></td></tr>}
          </tbody>
        </table>
      </Card>
    </div>
  )
}
