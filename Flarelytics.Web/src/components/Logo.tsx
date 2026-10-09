/**
 * Il segno di ElephantSight: una testa d'elefante di profilo, minimale, che è
 * anche una lente d'ingrandimento (la testa è la lente, la proboscide il
 * manico): l'elefante che vede, e ricorda, tutto delle tue app.
 *
 * I colori sono quelli del tema: il riquadro e i tagli che staccano l'orecchio
 * e l'occhio prendono il colore del pannello, quindi funziona in chiaro e in scuro.
 */
export function LogoMark({ className = "size-6" }: { className?: string }) {
  return (
    <svg viewBox="0 0 32 32" className={className} aria-hidden>
      <rect x=".5" y=".5" width="31" height="31" rx="7" fill="var(--color-panel-2)" stroke="var(--color-line-strong)" />
      {/* orecchio, dietro la testa */}
      <path d="M15.4 7C9.6 5.6 4.6 9.4 4.6 15.2c0 4.9 3.6 8.4 8.6 8.1z" fill="var(--color-brand)" />
      {/* testa (la lente): il bordo del colore del pannello la stacca dall'orecchio */}
      <circle cx="18" cy="13.8" r="7.3" fill="var(--color-brand)" stroke="var(--color-panel-2)" strokeWidth="1.6" />
      {/* proboscide (il manico), con la punta che si alza */}
      <path d="M22.4 18.6c1 3.2 2.5 5.5 4.9 6 1 .2 1.7-.4 1.7-1.3" fill="none" stroke="var(--color-brand)" strokeWidth="3.8" strokeLinecap="round" strokeLinejoin="round" />
      <circle cx="20.5" cy="12" r="1.65" fill="var(--color-panel-2)" />
    </svg>
  );
}

export function Logo({ withName = true }: { withName?: boolean }) {
  return (
    <span className="inline-flex items-center gap-2">
      <LogoMark />
      {withName && <span className="text-[0.9375rem] font-semibold tracking-tight text-fg">ElephantSight</span>}
    </span>
  );
}
