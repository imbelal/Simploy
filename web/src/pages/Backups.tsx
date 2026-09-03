import { useEffect, useState } from 'react'
import { api } from '../api'
import { Button, Card, EmptyState, Field, PageHeader, Panel, inputCls } from '../components/Field'

export default function Backups() {
  const [s, setS] = useState<any>(null)
  const [files, setFiles] = useState<any[]>([])
  const [msg, setMsg] = useState('')

  const load = async () => {
    const settings = await api.backups.get()
    setS(settings)
    try { setFiles(await api.backups.list()) } catch (e: any) { /* agent not on host */ }
  }
  useEffect(() => { load() }, [])

  const save = async () => { if (s) { await api.backups.set(s); setMsg('Saved'); load() } }
  const run = async () => { await api.backups.run(); setMsg('Backup triggered'); load() }
  const restore = async (file: string) => {
    if (!confirm('Restore this backup into the control-plane database? This overwrites current data. A backup is taken first.')) return
    setMsg('Restoring…'); 
    try { const r: any = await api.backups.restore(file); setMsg('Restored: ' + (r.result || file)) ; load() }
    catch (e: any) { setMsg(e.message) }
  }

  if (!s) return <EmptyState title="Loading…" />

  return (
    <div className="space-y-6">
      <PageHeader title="Backups" desc="Automatically back up Simploy's own control-plane database. Backups are pg_dump files stored on the server, pruned by retention." />

      <Panel>
        <div className="font-semibold text-slate-900 mb-4">Backup settings</div>
        <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
          <Field label="Enabled">
            <label className="flex items-center gap-2 mt-2 text-sm text-slate-700"><input type="checkbox" checked={s.enabled} onChange={e => setS({ ...s, enabled: e.target.checked })} className="rounded text-indigo-600" /> Scheduled backups</label>
          </Field>
          <Field label="Interval (minutes)" hint="How often to back up (>= 5). 1440 = daily.">
            <input className={inputCls} type="number" value={s.intervalMinutes} onChange={e => setS({ ...s, intervalMinutes: +e.target.value })} />
          </Field>
          <Field label="Retention (files)" hint="Keep this many dumps, delete older ones.">
            <input className={inputCls} type="number" value={s.retention} onChange={e => setS({ ...s, retention: +e.target.value })} />
          </Field>
          <Field label="Destination dir" hint="On the server, e.g. /opt/simploy/backups">
            <input className={inputCls} value={s.destDir} onChange={e => setS({ ...s, destDir: e.target.value })} />
          </Field>
        </div>
        <div className="flex items-center gap-3 mt-4">
          <Button variant="primary" onClick={save}>Save</Button>
          <Button variant="secondary" onClick={run}>Back up now</Button>
          {msg && <div className="text-xs text-slate-500">{msg}</div>}
          <span className="text-xs text-slate-400 ml-auto">Last backup: {s.lastBackupAt ? new Date(s.lastBackupAt).toLocaleString() : 'never'}</span>
        </div>
      </Panel>

      <Card title={`Backups (${files.length})`}>
        <table className="w-full text-sm">
          <thead className="bg-slate-50 text-slate-500"><tr><th className="text-left px-5 py-3 font-medium">File</th><th className="text-left px-4 py-3 font-medium">Created</th><th className="text-left px-4 py-3 font-medium">Size</th><th className="text-left px-4 py-3 font-medium">Actions</th></tr></thead>
          <tbody>
            {files.map((f: any) => (
              <tr key={f.name} className="border-t border-slate-100">
                <td className="px-5 py-2.5 font-mono text-xs text-slate-700">{f.name}</td>
                <td className="px-4 py-2.5 text-xs text-slate-500">{new Date(f.created).toLocaleString()}</td>
                <td className="px-4 py-2.5 text-xs text-slate-500">{(f.size / 1024).toFixed(1)} KB</td>
                <td className="px-4 py-2.5"><Button size="sm" variant="danger" onClick={() => restore(f.name)}>Restore</Button></td>
              </tr>
            ))}
            {files.length === 0 && <tr><td colSpan={4}><EmptyState title="No backups yet">Enable backups or click “Back up now”.</EmptyState></td></tr>}
          </tbody>
        </table>
      </Card>
    </div>
  )
}
