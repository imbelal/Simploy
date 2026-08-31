import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api'
import { Badge, Button, Card, EmptyState, Field, PageHeader, Panel, inputCls } from '../components/Field'

export default function Projects() {
  const [projects, setProjects] = useState<any[]>([])
  const [servers, setServers] = useState<any[]>([])
  const [form, setForm] = useState({ name: 'BdShopManager', slug: 'bdshopmanager', imageRepository: 'ghcr.io/imbelal/bdshopmanager', gitRepository: 'https://github.com/imbelal/BdShopManager', gitToken: '', registryUsername: '', registryPassword: '', dockerfilePath: 'src/WebApi/Dockerfile', dockerContext: '.' })
  const [showPrivate, setShowPrivate] = useState(false)
  const [msg, setMsg] = useState('')
  const [envEdit, setEnvEdit] = useState<any>(null)
  const [envText, setEnvText] = useState('')
  const [domEdit, setDomEdit] = useState<any>(null)
  const [domText, setDomText] = useState('')

  const openEnvEdit = (e: any) => {
    setEnvEdit(e)
    setEnvText(Object.entries(e.envVars || {}).map(([k, v]) => `${k}=${v}`).join('\n'))
  }
  const saveEnv = async () => {
    if (!envEdit) return
    const vars: Record<string, string> = {}
    for (const line of envText.split('\n')) {
      const t = line.trim()
      if (!t) continue
      const i = t.indexOf('=')
      if (i <= 0) continue
      vars[t.slice(0, i).trim()] = t.slice(i + 1)
    }
    await api.envs.setEnv(envEdit.id, vars)
    setEnvEdit(null)
    setMsg(`Env vars saved for ${envEdit.name}`)
    load()
  }

  const openDomEdit = (e: any) => {
    setDomEdit(e)
    setDomText((e.domains || []).map((d: any) => (d.targetPort ? `${d.host} ${d.targetPort}` : d.host)).join('\n'))
  }
  const saveDomains = async () => {
    if (!domEdit) return
    const domains: any[] = []
    for (const line of domText.split('\n')) {
      const t = line.trim()
      if (!t) continue
      const [host, port] = t.split(/\s+/).slice(0, 2)
      if (!host) continue
      domains.push({ host, targetPort: port ? +port : 80, isStatic: false, weighted: false, weight: 0 })
    }
    await api.envs.setDomains(domEdit.id, domains)
    setDomEdit(null)
    setMsg(`Domains saved for ${domEdit.name}`)
    load()
  }

  const load = () => {
    api.projects.list().then(setProjects).catch(e => setMsg(e.message))
    api.servers.list().then(setServers).catch(() => { })
  }
  useEffect(() => { load() }, [])

  const create = async () => {
    if (!form.slug || !form.imageRepository) return setMsg('Slug and image repository are required')
    if (!form.gitRepository && !form.imageRepository) return setMsg('Add either a Git repo (to build) or an image repository (to pull)')
    await api.projects.create(form)
    setMsg('Project created' + (form.gitToken || form.registryPassword ? ' with private auth' : ' (public)'))
    load()
  }
  const delProject = async (id: string) => {
    if (!confirm('Delete project + all its environments?')) return
    await api.projects.del(id)
    setMsg('Project deleted')
    load()
  }
  const delEnv = async (id: string) => {
    if (!confirm('Delete environment?')) return
    await api.envs.del(id)
    setMsg('Environment deleted')
    load()
  }
  const createEnv = async (projectId: string) => {
    if (servers.length === 0) return setMsg('Add a server in Servers first')
    const name = prompt('Environment name: production or staging', 'production') || 'production'
    const serverId = servers.length === 1 ? servers[0].id : prompt(`Pick server id from:\n${servers.map(s => `${s.name}=${s.id.slice(0, 8)} (${s.host})`).join('\n')}`, servers[0].id) || servers[0].id
    const slot = name === 'production' ? 'prod' : 'staging'
    await api.envs.create({ projectId, serverId, name, slot, imageTag: slot })
    setMsg(`Environment ${name} added on ${slot}`)
    load()
  }

  return (
    <div className="space-y-6">
      <PageHeader title="Projects" desc="A project is one app. Point Simploy at its Git repo (build from source) or a registry image (pull). Each project can be deployed to many servers." />

      <Panel>
        <div className="font-semibold text-slate-900 mb-4">Create a project</div>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <Field label="Project name" hint="Display name, e.g. BdShopManager">
            <input className={inputCls} value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} />
          </Field>
          <Field label="Slug" hint="Unique id; used in container and folder names. Lowercase + dashes.">
            <input className={inputCls} value={form.slug} onChange={e => setForm({ ...form, slug: e.target.value.toLowerCase().replace(/\s+/g, '-') })} />
          </Field>
          <Field label="Image repository" hint="Where the built image is tagged, e.g. ghcr.io/you/app">
            <input className={inputCls} value={form.imageRepository} onChange={e => setForm({ ...form, imageRepository: e.target.value })} />
          </Field>
          <Field label="Git repository" hint="URL to build from source. Leave blank to only pull a prebuilt image.">
            <input className={inputCls} placeholder="https://github.com/org/app" value={form.gitRepository} onChange={e => setForm({ ...form, gitRepository: e.target.value })} />
          </Field>
          <Field label="Dockerfile path" hint="Where the Dockerfile is in your repo (relative to repo root).">
            <input className={inputCls} value={form.dockerfilePath} onChange={e => setForm({ ...form, dockerfilePath: e.target.value })} />
          </Field>
          <Field label="Build context" hint="Folder passed as the docker build context (usually .).">
            <input className={inputCls} value={form.dockerContext} onChange={e => setForm({ ...form, dockerContext: e.target.value })} />
          </Field>
        </div>

        <label className="flex items-center gap-2.5 mt-5 text-sm font-medium text-slate-700 cursor-pointer">
          <input type="checkbox" checked={showPrivate} onChange={e => setShowPrivate(e.target.checked)} className="rounded text-indigo-600 focus:ring-indigo-500" />
          This is a private repo / private registry — let me set access tokens
        </label>

        {showPrivate && (
          <div className="mt-3 p-4 bg-amber-50/60 border border-amber-200 rounded-xl space-y-4">
            <div className="text-xs text-amber-800 leading-relaxed">
              <b>Private Git repo</b> → a token with <code>repo</code> scope (classic PAT or fine-grained). <b>Private registry</b> (e.g. GHCR) → username + PAT with <code>read:packages</code>. Leave blank for public repos/images.
            </div>
            <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
              <Field label="Git token" hint="Used for git clone of the private repo.">
                <input className={inputCls} placeholder="ghp_... or github_pat_..." type="password" value={form.gitToken} onChange={e => setForm({ ...form, gitToken: e.target.value })} />
              </Field>
              <Field label="Registry username">
                <input className={inputCls} placeholder="ghcr user" value={form.registryUsername} onChange={e => setForm({ ...form, registryUsername: e.target.value })} />
              </Field>
              <Field label="Registry password / token" hint="Used by the Agent to pull the private image.">
                <input className={inputCls} placeholder="PAT with read:packages" type="password" value={form.registryPassword} onChange={e => setForm({ ...form, registryPassword: e.target.value })} />
              </Field>
            </div>
          </div>
        )}

        <div className="flex items-center gap-3 mt-4">
          <Button variant="primary" onClick={create}>Create project</Button>
          {msg && <div className="text-xs text-slate-500 font-mono">{msg}</div>}
        </div>
      </Panel>

      <div className="space-y-4">
        {projects.map(p => (
          <Card key={p.id} title={<span className="flex items-center gap-2 flex-wrap">{p.name}</span>} subtitle={<span className="flex items-center gap-2 flex-wrap">
            <span className="font-mono text-[11px] bg-slate-100 text-slate-600 px-1.5 py-0.5 rounded">{p.slug}</span>
            <span className="font-mono text-[11px] text-slate-500">{p.imageRepository}</span>
          </span>}
          action={<div className="flex gap-2 shrink-0"><Button size="sm" onClick={() => createEnv(p.id)}>+ Environment</Button><Button size="sm" variant="danger" onClick={() => delProject(p.id)}>Delete</Button></div>}
          >
            <div className="px-5 py-4">
              <div className="flex items-center gap-2 flex-wrap text-xs text-slate-500">
                {p.dockerfilePath && <span className="mr-4">Dockerfile: <code className="bg-slate-100 px-1.5 rounded font-mono text-[11px]">{p.dockerfilePath}</code></span>}
                <span>Context: <code className="bg-slate-100 px-1.5 rounded font-mono text-[11px]">{p.dockerContext || '.'}</code></span>
                {p.gitRepository && <span className="font-mono text-[11px] text-slate-400">← {p.gitRepository}</span>}
                {(p.gitToken || p.registryPassword) && <Badge tone="amber">private</Badge>}
              </div>

              <div className="mt-4">
                <div className="text-xs font-semibold text-slate-500 uppercase tracking-wide mb-2">Environments ({p.environments?.length || 0})</div>
                <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
                  {(p.environments || []).map((e: any) => (
                    <div key={e.id} className="rounded-xl border border-slate-200/70 bg-slate-50/60 p-3.5 relative transition hover:border-indigo-200 hover:bg-indigo-50/30">
                      <button onClick={() => delEnv(e.id)} className="absolute top-2.5 right-2.5 text-slate-300 hover:text-red-500 transition">✕</button>
                      <div className="font-medium text-sm flex items-center gap-2 pr-6 text-slate-800">{e.name} <span className="text-[11px] bg-white border px-1.5 py-0.5 rounded font-mono text-slate-500">{e.slot}/{e.imageTag}</span></div>
                      <div className="text-xs text-slate-500 mt-1.5">Server: <span className="font-mono text-[11px]">{e.server?.name} ({e.server?.host})</span></div>
                      <div className="text-xs text-slate-500 mt-0.5">Domains: {e.domains?.map((d: any) => d.host).join(', ') || '—'}</div>
                      <div className="flex items-center gap-2 mt-2">
                        <Link to="/deployments" className="text-xs text-indigo-600 hover:underline font-medium">Deploy →</Link>
                        <button onClick={() => openEnvEdit(e)} className="text-xs text-indigo-600 hover:underline font-medium">Env vars</button>
                        <button onClick={() => openDomEdit(e)} className="text-xs text-indigo-600 hover:underline font-medium">Domains</button>
                      </div>
                    </div>
                  ))}
                  {(p.environments || []).length === 0 && <div className="text-sm text-slate-400 col-span-2 py-5 text-center border border-dashed border-slate-200 rounded-xl">No environments yet — click “+ Environment”</div>}
                </div>
              </div>
            </div>
          </Card>
        ))}
        {projects.length === 0 && <Card title="Projects"><EmptyState title="No projects yet">Create one above. A demo project is pre-loaded.</EmptyState></Card>}
      </div>

      {envEdit && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/40 backdrop-blur-sm p-4">
          <div className="bg-white rounded-2xl shadow-deep w-full max-w-lg p-5">
            <div className="font-semibold text-slate-900 mb-1">Env vars — {envEdit.name}</div>
            <div className="text-xs text-slate-500 mb-3">One per line as <code className="bg-slate-100 px-1 rounded font-mono">KEY=VALUE</code>. Written to the app's <code className="bg-slate-100 px-1 rounded font-mono">.env</code> during deploy. Leave blank to clear.</div>
            <textarea value={envText} onChange={e => setEnvText(e.target.value)} rows={10} className={inputCls + ' font-mono text-xs'} placeholder={'MSSQL_SA_PASSWORD=YourStr0ngPass!\nACCEPT_EULA=Y\n' + 'JWT_SECRET=...'} />
            <div className="flex justify-end gap-3 mt-4">
              <Button variant="secondary" onClick={() => setEnvEdit(null)}>Cancel</Button>
              <Button variant="primary" onClick={saveEnv}>Save</Button>
            </div>
          </div>
        </div>
      )}

      {domEdit && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/40 backdrop-blur-sm p-4">
          <div className="bg-white rounded-2xl shadow-deep w-full max-w-md p-5">
            <div className="font-semibold text-slate-900 mb-1">Domains — {domEdit.name}</div>
            <div className="text-xs text-slate-500 mb-3">One per line as <code className="bg-slate-100 px-1 rounded font-mono">domain.tld</code> (or <code className="bg-slate-100 px-1 rounded font-mono">domain.tld 8080</code> to set the port Caddy proxies to).</div>
            <textarea value={domText} onChange={e => setDomText(e.target.value)} rows={6} className={inputCls + ' font-mono text-xs'} placeholder={'api.example.com 8080'} />
            <div className="flex justify-end gap-3 mt-4">
              <Button variant="secondary" onClick={() => setDomEdit(null)}>Cancel</Button>
              <Button variant="primary" onClick={saveDomains}>Save</Button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
