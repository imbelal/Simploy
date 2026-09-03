import { useEffect, useState } from 'react'
import { api } from '../api'
import { toast } from '../toast'
import { Badge, Button, Card, EmptyState, Field, PageHeader, Panel, Skeleton, inputCls } from '../components/Field'

/// <summary>Modal that streams `docker logs -f` for a container via SSE.</summary>
function ContainerLogsModal({ serverId, name, onClose }: { serverId: string; name: string; onClose: () => void }) {
  const [log, setLog] = useState('')
  const [err, setErr] = useState('')
  const [tail, setTail] = useState(200)

  useEffect(() => {
    setLog(''); setErr('')
    let cancelled = false
    const token = localStorage.getItem('simploy.jwt')
    const run = async () => {
      try {
        const resp = await fetch(`/api/servers/${serverId}/containers/${encodeURIComponent(name)}/logs?tail=${tail}`, {
          headers: token ? { Authorization: `Bearer ${token}` } : {}
        })
        if (!resp.ok || !resp.body) { setErr(`HTTP ${resp.status}`); return }
        const reader = resp.body.getReader(); const dec = new TextDecoder(); let buf = ''
        while (!cancelled) {
          const { done, value } = await reader.read()
          if (done) break
          buf += dec.decode(value, { stream: true })
          let i
          while ((i = buf.indexOf('\n\n')) !== -1) {
            const block = buf.slice(0, i); buf = buf.slice(i + 2)
            for (const ln of block.split('\n')) {
              const m = ln.match(/^data:\s?(.*)$/)
              if (m) setLog(prev => prev + m[1] + '\n')
            }
          }
        }
      } catch (e: any) { setErr(e?.message || 'stream failed') }
    }
    run()
    return () => { cancelled = true }
  }, [serverId, name, tail])

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/40 backdrop-blur-sm p-4">
      <div className="bg-slate-900 rounded-2xl border border-white/10 shadow-deep w-full max-w-3xl overflow-hidden">
        <div className="flex items-center justify-between px-4 py-2.5 border-b border-white/10 bg-white/5">
          <div className="text-sm text-slate-200 font-medium">
            Logs — <span className="font-mono">{name}</span>
          </div>
          <div className="flex items-center gap-3">
            <label className="text-xs text-slate-400 flex items-center gap-1">
              tail
              <input type="number" min={10} max={10000} value={tail} onChange={e => setTail(Math.max(10, +e.target.value || 200))}
                className="w-20 bg-white/5 text-slate-200 text-xs px-2 py-1 rounded border border-white/10" />
            </label>
            <button onClick={onClose} className="text-slate-400 hover:text-white text-xs">✕</button>
          </div>
        </div>
        {err
          ? <pre className="p-4 text-xs font-mono text-red-300">Error: {err}</pre>
          : <pre className="p-4 text-xs font-mono text-slate-100 overflow-auto max-h-[60vh] whitespace-pre-wrap">{log || 'Streaming…'}</pre>}
      </div>
    </div>
  )
}

function ServerStatus({ status }: { status: number | string }) {
  const m: Record<number | string, 'pending' | 'ok' | 'slate' | 'red'> = {
    0: 'pending', Pending: 'pending',
    1: 'ok', Online: 'ok',
    2: 'slate', Offline: 'slate',
    3: 'red', Unreachable: 'red',
  }
  const label = typeof status === 'number' ? ['Pending', 'Online', 'Offline', 'Unreachable'][status] : status
  return <Badge tone={m[status] || 'slate'}>{label}</Badge>
}

export default function Servers() {
  const [servers, setServers] = useState<any[]>([])
  const [form, setForm] = useState({ name: '', host: '', sshPort: 22, sshUser: 'root' })
  const [busy, setBusy] = useState(false)
  const [loaded, setLoaded] = useState(false)
  const [certs, setCerts] = useState<{ serverId: string; host: string; list: any[] } | null>(null)
  const [certsBusy, setCertsBusy] = useState<string | null>(null)
  const [metrics, setMetrics] = useState<{ serverId: string; host: string; list: any[] } | null>(null)
  const [metricsBusy, setMetricsBusy] = useState<string | null>(null)
  const [containers, setContainers] = useState<{ serverId: string; host: string; list: any[] } | null>(null)
  const [containersBusy, setContainersBusy] = useState<string | null>(null)
  const [containerActionBusy, setContainerActionBusy] = useState<string | null>(null)
  const [logsFor, setLogsFor] = useState<{ serverId: string; name: string } | null>(null)

  const load = () => api.servers.list().then(s => { setServers(s); setLoaded(true) }).catch(e => toast(e.message, 'error'))
  useEffect(() => { load() }, [])

  const certTone = (d: number): string => d <= 7 ? 'text-red-600' : d <= 30 ? 'text-amber-600' : 'text-emerald-600'

  const openCerts = async (id: string, host: string) => {
    setCertsBusy(id)
    try { const list = await api.servers.certificates(id); setCerts({ serverId: id, host, list }) }
    catch (e: any) { toast(e.message, 'error') } finally { setCertsBusy(null) }
  }

  const openMetrics = async (id: string, host: string) => {
    setMetricsBusy(id)
    try { const list = await api.servers.metrics(id); setMetrics({ serverId: id, host, list }) }
    catch (e: any) { toast(e.message, 'error') } finally { setMetricsBusy(null) }
  }

  const openContainers = async (id: string, host: string) => {
    setContainersBusy(id)
    try { const list = await api.servers.containers(id); setContainers({ serverId: id, host, list }) }
    catch (e: any) { toast(e.message, 'error') } finally { setContainersBusy(null) }
  }

  const containerAction = async (id: string, host: string, name: string, action: 'start'|'stop'|'restart'|'delete') => {
    if (action === 'delete' && !confirm(`Delete container ${name}? Compose will recreate it on the next deploy.`)) return
    const key = `${id}:${name}:${action}`
    setContainerActionBusy(key)
    try {
      const r: any = await api.servers.containerAction(id, name, action)
      toast(`${action} ${name}: ${r.ok ? 'ok' : 'failed'}`, r.ok ? 'success' : 'error')
      // Refresh the list so state changes are visible.
      const list = await api.servers.containers(id)
      setContainers({ serverId: id, host, list })
    }
    catch (e: any) { toast(e.message, 'error') }
    finally { setContainerActionBusy(null) }
  }

  const create = async () => {
    if (!form.name || !form.host) return toast('Name and Host are required', 'error')
    setBusy(true)
    try { await api.servers.create(form); setForm({ name: '', host: '', sshPort: 22, sshUser: 'root' }); toast('Server added'); load() }
    catch (e: any) { toast(e.message, 'error') } finally { setBusy(false) }
  }
  const check = async (id: string) => {
    try { const r: any = await api.servers.check(id); toast(`Check: ${JSON.stringify(r)}`, 'info'); load() }
    catch (e: any) { toast(e.message, 'error') }
  }
  const del = async (id: string) => {
    if (!confirm('Delete server? Environments using it will need reassignment.')) return
    try { await api.servers.del(id); toast('Server deleted'); load() }
    catch (e: any) { toast(e.message, 'error') }
  }

  return (
    <div className="space-y-6">
      <PageHeader title="Servers" desc="Each server is a VM that runs the Simploy Agent on port 8089. The Agent builds and runs your apps there." />

      <Panel>
        <div className="font-semibold text-slate-900 mb-4">Add a server</div>
        <div className="grid grid-cols-2 lg:grid-cols-4 gap-4">
          <Field label="Name" hint="A label you'll recognise, e.g. prod-vm">
            <input className={inputCls} placeholder="prod-vm" value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} />
          </Field>
          <Field label="Host / IP" hint="Where the Agent is reachable, e.g. 65.108.1.10">
            <input className={inputCls} placeholder="65.108.x.x" value={form.host} onChange={e => setForm({ ...form, host: e.target.value })} />
          </Field>
          <Field label="SSH port">
            <input className={inputCls} type="number" value={form.sshPort} onChange={e => setForm({ ...form, sshPort: +e.target.value })} />
          </Field>
          <Field label="SSH user">
            <input className={inputCls} placeholder="root" value={form.sshUser} onChange={e => setForm({ ...form, sshUser: e.target.value })} />
          </Field>
        </div>
        <div className="flex items-center gap-3 mt-4">
          <Button variant="primary" onClick={create} loading={busy}>Add server</Button>
        </div>
      </Panel>

      <Card title={`Servers (${servers.length})`} subtitle="Green = Agent reachable on :8089">
        <table className="w-full text-sm">
          <thead className="bg-slate-50 text-slate-500"><tr><th className="text-left px-5 py-3 font-medium">Name</th><th className="text-left px-4 py-3 font-medium">Host</th><th className="text-left px-4 py-3 font-medium">Status</th><th className="text-left px-4 py-3 font-medium">Actions</th></tr></thead>
          <tbody>
            {!loaded ? (
              <tr><td colSpan={4} className="px-4 py-6"><div className="space-y-3">{Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-4 w-3/4" />)}</div></td></tr>
            ) : servers.map(s => (
              <tr key={s.id} className="border-t border-slate-100 hover:bg-slate-50/60 transition">
                <td className="px-5 py-3 font-medium text-slate-800">{s.name}</td>
                <td className="px-4 py-3 font-mono text-xs text-slate-600">{s.host}:{s.sshPort} <span className="text-slate-400">({s.sshUser})</span></td>
                <td className="px-4 py-3"><ServerStatus status={s.status} /></td>
                <td className="px-4 py-3 flex gap-2">
                  <Button size="sm" variant="secondary" onClick={() => check(s.id)}>Check :8089</Button>
                  <Button size="sm" variant="secondary" loading={certsBusy === s.id} onClick={() => openCerts(s.id, s.host)}>Certs</Button>
                  <Button size="sm" variant="secondary" loading={metricsBusy === s.id} onClick={() => openMetrics(s.id, s.host)}>Metrics</Button>
                  <Button size="sm" variant="secondary" loading={containersBusy === s.id} onClick={() => openContainers(s.id, s.host)}>Containers</Button>
                  <Button size="sm" variant="danger" onClick={() => del(s.id)}>Delete</Button>
                </td>
              </tr>
            ))}
            {loaded && servers.length === 0 && <tr><td colSpan={4}><EmptyState title="No servers yet">Add one above, or run the one-liner install on your VM first.</EmptyState></td></tr>}
            {metrics && (
              <tr className="border-t border-slate-100 bg-slate-50/50">
                <td colSpan={4} className="px-5 py-4">
                  <div className="flex items-center justify-between mb-2">
                    <div className="text-sm font-medium text-slate-700">Container metrics on {metrics.host}</div>
                    <div className="flex items-center gap-3">
                      <button className="text-xs text-indigo-600 hover:underline" onClick={() => openMetrics(metrics.serverId, metrics.host)}>Refresh</button>
                      <button className="text-xs text-slate-400 hover:text-slate-600" onClick={() => setMetrics(null)}>Close</button>
                    </div>
                  </div>
                  <table className="w-full text-sm">
                    <thead className="text-slate-400 text-xs"><tr><th className="text-left py-1 font-medium">Container</th><th className="text-right py-1 font-medium">CPU %</th><th className="text-right py-1 font-medium">Memory</th><th className="text-right py-1 font-medium">Mem %</th><th className="text-right py-1 font-medium">PIDs</th></tr></thead>
                    <tbody>
                      {metrics.list.length === 0 ? (
                        <tr><td className="py-2 text-xs text-slate-400" colSpan={5}>No containers reporting metrics.</td></tr>
                      ) : metrics.list.map((c, i) => (
                        <tr key={i} className="border-t border-slate-100">
                          <td className="py-1.5 font-mono text-xs text-slate-700">{c.name}</td>
                          <td className="py-1.5 text-right text-xs text-slate-600">{c.cpuPerc}</td>
                          <td className="py-1.5 text-right text-xs text-slate-600">{c.memUsage}</td>
                          <td className="py-1.5 text-right text-xs text-slate-600">{c.memPerc}</td>
                          <td className="py-1.5 text-right text-xs text-slate-600">{c.pids}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </td>
              </tr>
            )}
            {certs && (
              <tr className="border-t border-slate-100 bg-slate-50/50">
                <td colSpan={4} className="px-5 py-4">
                  <div className="flex items-center justify-between mb-2">
                    <div className="text-sm font-medium text-slate-700">TLS certificates on {certs.host}</div>
                    <button className="text-xs text-slate-400 hover:text-slate-600" onClick={() => setCerts(null)}>Close</button>
                  </div>
                  <table className="w-full text-sm">
                    <thead className="text-slate-400 text-xs"><tr><th className="text-left py-1 font-medium">Domain</th><th className="text-left py-1 font-medium">Issuer</th><th className="text-left py-1 font-medium">Expires</th><th className="text-left py-1 font-medium">Days left</th></tr></thead>
                    <tbody>
                      {certs.list.length === 0 ? (
                        <tr><td className="py-2 text-xs text-slate-400" colSpan={4}>No certificates found (Caddy may still be obtaining them).</td></tr>
                      ) : certs.list.map((c, i) => (
                        <tr key={i} className="border-t border-slate-100">
                          <td className="py-1.5 font-mono text-xs text-slate-700">{c.domain}</td>
                          <td className="py-1.5 text-xs text-slate-500">{c.issuer}</td>
                          <td className="py-1.5 text-xs text-slate-500">{new Date(c.notAfter).toLocaleString()}</td>
                          <td className={`py-1.5 text-xs font-medium ${certTone(c.daysLeft)}`}>{c.daysLeft} days</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </td>
              </tr>
            )}
            {containers && (
              <tr className="border-t border-slate-100 bg-slate-50/50">
                <td colSpan={4} className="px-5 py-4">
                  <div className="flex items-center justify-between mb-2">
                    <div className="text-sm font-medium text-slate-700">Containers on {containers.host}</div>
                    <div className="flex items-center gap-3">
                      <button className="text-xs text-indigo-600 hover:underline" onClick={() => openContainers(containers.serverId, containers.host)}>Refresh</button>
                      <button className="text-xs text-slate-400 hover:text-slate-600" onClick={() => setContainers(null)}>Close</button>
                    </div>
                  </div>
                  <table className="w-full text-sm">
                    <thead className="text-slate-400 text-xs"><tr>
                      <th className="text-left py-1 font-medium">Container</th>
                      <th className="text-left py-1 font-medium">Image</th>
                      <th className="text-left py-1 font-medium">State</th>
                      <th className="text-left py-1 font-medium">Project</th>
                      <th className="text-right py-1 font-medium">Actions</th>
                    </tr></thead>
                    <tbody>
                      {containers.list.length === 0 ? (
                        <tr><td className="py-2 text-xs text-slate-400" colSpan={5}>No containers running on this server.</td></tr>
                      ) : containers.list.map((c: any, i: number) => {
                        const isCp = (c.name || '').startsWith('simploy-')
                        const running = c.state === 'running'
                        const key = (a: string) => `${containers.serverId}:${c.name}:${a}`
                        return (
                          <tr key={i} className="border-t border-slate-100">
                            <td className="py-1.5 font-mono text-xs text-slate-700">{c.name}</td>
                            <td className="py-1.5 text-xs text-slate-500 truncate max-w-xs" title={c.image}>{c.image}</td>
                            <td className="py-1.5 text-xs"><span className={running ? 'text-emerald-600' : 'text-slate-400'}>{c.state}</span></td>
                            <td className="py-1.5 text-xs text-slate-500">{c.project || '—'}</td>
                            <td className="py-1.5 text-right">
                              <div className="inline-flex gap-1">
                                <button className="px-2 py-0.5 text-xs rounded bg-indigo-50 text-indigo-700 hover:bg-indigo-100" onClick={() => setLogsFor({ serverId: containers.serverId, name: c.name })}>Logs</button>
                                {!running && <button className="px-2 py-0.5 text-xs rounded bg-emerald-50 text-emerald-700 hover:bg-emerald-100 disabled:opacity-50" disabled={isCp || containerActionBusy === key('start')} onClick={() => containerAction(containers.serverId, containers.host, c.name, 'start')}>Start</button>}
                                {running && <button className="px-2 py-0.5 text-xs rounded bg-amber-50 text-amber-700 hover:bg-amber-100 disabled:opacity-50" disabled={isCp || containerActionBusy === key('stop')} onClick={() => containerAction(containers.serverId, containers.host, c.name, 'stop')}>Stop</button>}
                                <button className="px-2 py-0.5 text-xs rounded bg-slate-100 text-slate-700 hover:bg-slate-200 disabled:opacity-50" disabled={isCp || containerActionBusy === key('restart')} onClick={() => containerAction(containers.serverId, containers.host, c.name, 'restart')}>Restart</button>
                                <button className="px-2 py-0.5 text-xs rounded bg-red-50 text-red-700 hover:bg-red-100 disabled:opacity-50" disabled={isCp || containerActionBusy === key('delete')} onClick={() => containerAction(containers.serverId, containers.host, c.name, 'delete')}>Delete</button>
                              </div>
                            </td>
                          </tr>
                        )
                      })}
                    </tbody>
                  </table>
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </Card>
      {logsFor && <ContainerLogsModal serverId={logsFor.serverId} name={logsFor.name} onClose={() => setLogsFor(null)} />}
    </div>
  )
}
