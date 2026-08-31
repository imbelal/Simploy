import type { ReactNode } from 'react'

export const inputCls =
  'w-full border border-slate-200 rounded-lg px-3 py-2 text-sm bg-white text-slate-900 placeholder:text-slate-400 focus:outline-none focus:ring-2 focus:ring-indigo-500/30 focus:border-indigo-400 transition shadow-sm'

type ButtonProps = {
  children: ReactNode
  onClick?: () => void
  variant?: 'primary' | 'secondary' | 'danger' | 'ghost'
  size?: 'sm' | 'md'
  type?: 'button' | 'submit'
  title?: string
  disabled?: boolean
  className?: string
}

const btnVariants: Record<string, string> = {
  primary: 'bg-gradient-to-br from-indigo-600 to-sky-500 text-white shadow-md shadow-indigo-500/20 hover:shadow-lg hover:shadow-indigo-500/30 hover:-translate-y-px',
  secondary: 'bg-white text-slate-700 border border-slate-200 hover:bg-slate-50 hover:border-slate-300',
  danger: 'bg-white text-red-600 border border-red-200 hover:bg-red-50 hover:border-red-300',
  ghost: 'text-slate-500 hover:text-slate-900 hover:bg-slate-100',
}

export function Button({ children, onClick, variant = 'secondary', size = 'md', type = 'button', title, disabled, className = '' }: ButtonProps) {
  return (
    <button
      type={type}
      onClick={onClick}
      title={title}
      disabled={disabled}
      className={`inline-flex items-center justify-center gap-1.5 font-medium rounded-lg transition active:scale-[0.98] disabled:opacity-50 disabled:pointer-events-none ${size === 'sm' ? 'px-3 py-1.5 text-xs' : 'px-4 py-2 text-sm'} ${btnVariants[variant]} ${className}`}
    >
      {children}
    </button>
  )
}

type BadgeProps = { children: ReactNode; tone?: 'green' | 'amber' | 'blue' | 'red' | 'slate' | 'violet' | 'ok' | 'pending' }
const badgeTones: Record<string, string> = {
  green: 'bg-emerald-50 text-emerald-700 ring-emerald-600/20',
  ok: 'bg-emerald-50 text-emerald-700 ring-emerald-600/20',
  amber: 'bg-amber-50 text-amber-700 ring-amber-600/20',
  pending: 'bg-amber-50 text-amber-700 ring-amber-600/20',
  blue: 'bg-sky-50 text-sky-700 ring-sky-600/20',
  red: 'bg-red-50 text-red-700 ring-red-600/20',
  violet: 'bg-violet-50 text-violet-700 ring-violet-600/20',
  slate: 'bg-slate-100 text-slate-600 ring-slate-500/20',
}
const dotTones: Record<string, string> = {
  green: 'bg-emerald-500', ok: 'bg-emerald-500',
  amber: 'bg-amber-500', pending: 'bg-amber-500',
  blue: 'bg-sky-500', red: 'bg-red-500', violet: 'bg-violet-500', slate: 'bg-slate-400',
}

export function Badge({ children, tone = 'slate' }: BadgeProps) {
  return (
    <span className={`inline-flex items-center gap-1.5 px-2.5 py-0.5 rounded-full text-xs font-medium ring-1 ring-inset ${badgeTones[tone]}`}>
      <span className={`w-1.5 h-1.5 rounded-full ${dotTones[tone]}`} />
      {children}
    </span>
  )
}

export function Card({ title, subtitle, action, children }: { title?: ReactNode; subtitle?: ReactNode; action?: ReactNode; children: ReactNode }) {
  return (
    <div className="bg-white rounded-2xl border border-slate-200/70 shadow-card overflow-hidden">
      {(title || action) && (
        <div className="flex items-center justify-between gap-4 px-5 py-4 border-b border-slate-100">
          <div>
            <div className="font-semibold text-slate-900">{title}</div>
            {subtitle && <div className="text-xs text-slate-500 mt-0.5">{subtitle}</div>}
          </div>
          {action}
        </div>
      )}
      {children}
    </div>
  )
}

export function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <label className="block">
      <span className="block text-xs font-semibold text-slate-600 mb-1.5">{label}</span>
      {children}
      {hint && <span className="block text-xs text-slate-400 mt-1 leading-relaxed">{hint}</span>}
    </label>
  )
}

export function Panel({ children }: { children: ReactNode }) {
  return <div className="bg-white rounded-2xl border border-slate-200/70 shadow-card p-5">{children}</div>
}

export function EmptyState({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="px-6 py-12 text-center">
      <div className="mx-auto w-10 h-10 rounded-xl bg-slate-100 text-slate-400 flex items-center justify-center text-lg mb-3">◌</div>
      <div className="text-sm font-medium text-slate-500">{title}</div>
      {children && <div className="text-xs text-slate-400 mt-1 max-w-sm mx-auto leading-relaxed">{children}</div>}
    </div>
  )
}

export function PageHeader({ title, desc, action }: { title: ReactNode; desc?: string; action?: ReactNode }) {
  return (
    <div className="flex items-start justify-between gap-4">
      <div>
        <h1 className="text-2xl font-bold tracking-tight text-slate-900">{title}</h1>
        {desc && <p className="text-slate-500 text-sm mt-1.5 max-w-3xl leading-relaxed">{desc}</p>}
      </div>
      {action}
    </div>
  )
}

export function StatCard({ label, value, sub, icon }: { label: string; value: number | string; sub: string; icon?: ReactNode }) {
  return (
    <div className="bg-white rounded-2xl border border-slate-200/70 shadow-card p-5 flex items-start gap-4 transition hover:shadow-card-hover">
      {icon && <div className="w-10 h-10 rounded-xl bg-gradient-to-br from-indigo-500 to-sky-500 text-white flex items-center justify-center text-lg shrink-0 shadow-sm">{icon}</div>}
      <div className="min-w-0">
        <div className="text-xs uppercase tracking-wide text-slate-400 font-medium">{label}</div>
        <div className="text-2xl font-bold text-slate-900 mt-0.5">{value}</div>
        <div className="text-xs text-slate-500 mt-0.5 truncate">{sub}</div>
      </div>
    </div>
  )
}
