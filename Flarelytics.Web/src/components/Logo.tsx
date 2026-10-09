// Un occhio che guarda gli store; la pupilla sono tre barre crescenti, le metriche.
export function Logo({ withName = true }: { withName?: boolean }) {
  return (
    <span className="inline-flex items-center gap-2">
      <svg viewBox="0 0 32 32" className="size-6" aria-hidden>
        <rect x=".5" y=".5" width="31" height="31" rx="7" fill="var(--color-panel-2)" stroke="var(--color-line-strong)" />
        <path
          d="M4.5 16C7.9 10.6 11.7 8 16 8s8.1 2.6 11.5 8C24.1 21.4 20.3 24 16 24s-8.1-2.6-11.5-8Z"
          fill="none"
          stroke="var(--color-brand)"
          strokeWidth="2"
          strokeLinejoin="round"
        />
        <circle cx="16" cy="16" r="5.5" fill="var(--color-brand)" />
        <path d="M12.9 18.6h1.6v-2.4h-1.6zM15.2 18.6h1.6v-4h-1.6zM17.5 18.6h1.6v-5.6h-1.6z" fill="var(--color-panel-2)" />
      </svg>
      {withName && <span className="text-[15px] font-semibold tracking-tight text-fg">WatchStore</span>}
    </span>
  );
}
