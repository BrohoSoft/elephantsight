export function Logo({ withName = true }: { withName?: boolean }) {
  return (
    <span className="inline-flex items-center gap-2">
      <svg viewBox="0 0 32 32" className="size-6" aria-hidden>
        <rect width="32" height="32" rx="7" fill="var(--color-panel-2)" stroke="var(--color-line-strong)" />
        <path d="M17.5 5 8 18h7l-1.5 9L24 13h-7.2L17.5 5Z" fill="var(--color-brand)" />
      </svg>
      {withName && <span className="text-[15px] font-semibold tracking-tight text-fg">Flarelytics</span>}
    </span>
  );
}
