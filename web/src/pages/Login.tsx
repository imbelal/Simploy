import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { auth } from '../api'
import { Button, Field, inputCls } from '../components/Field'

export default function Login() {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const nav = useNavigate()

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true); setError('')
    try {
      await auth.login(username.trim(), password)
      nav('/')
    } catch (e: any) {
      setError(e.message.includes('401') ? 'Invalid username or password' : e.message)
    } finally { setBusy(false) }
  }

  return (
    <div className="min-h-screen bg-slate-50 flex items-center justify-center p-4">
      <div className="pointer-events-none fixed inset-0 bg-[radial-gradient(800px_400px_at_50%_-80px,rgba(99,102,241,0.12),transparent)]" />
      <div className="w-full max-w-sm bg-white rounded-2xl border border-slate-200 shadow-card p-8 relative">
        <div className="flex items-center gap-3 mb-6">
          <div className="w-10 h-10 rounded-xl bg-gradient-to-br from-indigo-500 to-sky-500 flex items-center justify-center font-bold text-white shadow-md shadow-indigo-500/30">S</div>
          <div>
            <div className="font-bold text-slate-900 leading-none">Simploy</div>
            <div className="text-xs text-slate-500 mt-0.5">Deploy from Git</div>
          </div>
        </div>
        <form onSubmit={submit} className="space-y-4">
          <Field label="Username">
            <input className={inputCls} autoFocus value={username} onChange={e => setUsername(e.target.value)} placeholder="admin" autoComplete="username" />
          </Field>
          <Field label="Password">
            <input className={inputCls} type="password" value={password} onChange={e => setPassword(e.target.value)} placeholder="••••••••" autoComplete="current-password" />
          </Field>
          {error && <div className="text-xs bg-red-50 border border-red-200 text-red-600 rounded-lg p-2.5">{error}</div>}
          <Button type="submit" variant="primary" className="w-full" disabled={busy || !username || !password}>
            {busy ? 'Signing in…' : 'Sign in'}
          </Button>
        </form>
      </div>
    </div>
  )
}
