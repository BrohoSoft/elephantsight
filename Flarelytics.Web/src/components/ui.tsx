import * as Dialog from "@radix-ui/react-dialog";
import clsx from "clsx";
import { AlertTriangle, Check, CircleAlert, Copy, Info, Loader2, X } from "lucide-react";
import { forwardRef, useState, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes, type TextareaHTMLAttributes } from "react";

/*
 * I mattoni del pannello. Pochi, e tutti qui: lo stile di Supabase vive di
 * coerenza, cioè degli stessi bordi, raggi e altezze ovunque.
 */

type ButtonVariant = "primary" | "secondary" | "ghost" | "danger";

export const Button = forwardRef<
  HTMLButtonElement,
  ButtonHTMLAttributes<HTMLButtonElement> & { variant?: ButtonVariant; size?: "sm" | "md"; loading?: boolean; icon?: ReactNode }
>(({ variant = "secondary", size = "md", loading, icon, className, children, disabled, ...props }, ref) => (
  <button
    ref={ref}
    disabled={disabled || loading}
    className={clsx(
      "inline-flex items-center justify-center gap-1.5 rounded-md border font-medium whitespace-nowrap transition-colors disabled:cursor-not-allowed disabled:opacity-50",
      size === "sm" ? "h-7 px-2.5 text-xs" : "h-8 px-3 text-[0.8125rem]",
      variant === "primary" && "border-brand bg-brand text-brand-ink hover:bg-brand-strong hover:border-brand-strong",
      variant === "secondary" && "border-line-strong bg-panel-2 text-fg hover:bg-hover",
      variant === "ghost" && "border-transparent bg-transparent text-muted hover:bg-hover hover:text-fg",
      variant === "danger" && "border-bad/40 bg-bad/10 text-bad hover:bg-bad/20",
      className,
    )}
    {...props}
  >
    {loading ? <Loader2 className="size-3.5 animate-spin" /> : icon}
    {children}
  </button>
));
Button.displayName = "Button";

const fieldBase =
  "w-full rounded-md border border-line-strong bg-field px-2.5 text-[0.8125rem] text-fg placeholder:text-faint transition-colors focus:border-brand/70 focus:outline-none";

export const Input = forwardRef<HTMLInputElement, InputHTMLAttributes<HTMLInputElement>>(({ className, ...props }, ref) => (
  <input ref={ref} className={clsx(fieldBase, "h-8", className)} {...props} />
));
Input.displayName = "Input";

export const Textarea = forwardRef<HTMLTextAreaElement, TextareaHTMLAttributes<HTMLTextAreaElement>>(({ className, ...props }, ref) => (
  <textarea ref={ref} className={clsx(fieldBase, "py-2 font-mono text-xs", className)} {...props} />
));
Textarea.displayName = "Textarea";

export function Select({ className, ...props }: SelectHTMLAttributes<HTMLSelectElement>) {
  return <select className={clsx(fieldBase, "h-8 pr-7", className)} {...props} />;
}

/**
 * Etichetta, campo e sotto un suggerimento o l'errore. Suggerimento ed errore
 * stanno fuori dal <label>: dentro diventerebbero parte del nome del campo, e
 * un lettore di schermo leggerebbe "Codice SDI Per la fattura elettronica…"
 * come se fosse l'etichetta.
 */
export function Field({ label, hint, error, children }: { label: string; hint?: ReactNode; error?: string; children: ReactNode }) {
  return (
    <div className="space-y-1.5">
      <label className="block space-y-1.5">
        <span className="block text-xs font-medium text-muted">{label}</span>
        {children}
      </label>
      {error ? <p role="alert" className="text-xs text-bad">{error}</p> : hint ? <p className="text-xs text-faint">{hint}</p> : null}
    </div>
  );
}

export function Panel({ title, description, actions, children, className, footer }: {
  title?: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
  children: ReactNode;
  className?: string;
  footer?: ReactNode;
}) {
  return (
    <section className={clsx("overflow-hidden rounded-lg border border-line bg-panel", className)}>
      {(title || actions) && (
        <header className="flex items-start justify-between gap-4 border-b border-line px-4 py-3">
          <div className="min-w-0">
            {title && <h2 className="text-sm font-medium text-fg">{title}</h2>}
            {description && <p className="mt-0.5 text-xs text-muted">{description}</p>}
          </div>
          {actions && <div className="flex shrink-0 items-center gap-2">{actions}</div>}
        </header>
      )}
      <div>{children}</div>
      {footer && <footer className="flex justify-end gap-2 border-t border-line bg-panel-2/50 px-4 py-2.5">{footer}</footer>}
    </section>
  );
}

type Tone = "neutral" | "ok" | "warn" | "bad" | "brand" | "ios" | "android";

export function Badge({ tone = "neutral", children, className }: { tone?: Tone; children: ReactNode; className?: string }) {
  return (
    <span
      className={clsx(
        "inline-flex items-center gap-1 rounded border px-1.5 py-px text-[0.6875rem] font-medium whitespace-nowrap",
        tone === "neutral" && "border-line-strong bg-panel-2 text-muted",
        tone === "ok" && "border-ok/30 bg-ok/10 text-ok",
        tone === "warn" && "border-warn/30 bg-warn/10 text-warn",
        tone === "bad" && "border-bad/30 bg-bad/10 text-bad",
        tone === "brand" && "border-brand/30 bg-brand/10 text-brand-fg",
        tone === "ios" && "border-ios/30 bg-ios/10 text-ios",
        tone === "android" && "border-android/30 bg-android/10 text-android",
        className,
      )}
    >
      {children}
    </span>
  );
}

export function Alert({ tone = "warn", title, children }: { tone?: "warn" | "bad" | "ok" | "info"; title?: ReactNode; children?: ReactNode }) {
  const Icon = tone === "ok" ? Check : tone === "bad" ? CircleAlert : tone === "info" ? Info : AlertTriangle;
  return (
    <div
      className={clsx(
        "flex gap-2.5 rounded-md border px-3 py-2.5 text-[0.8125rem]",
        tone === "warn" && "border-warn/30 bg-warn/5",
        tone === "bad" && "border-bad/30 bg-bad/5",
        tone === "ok" && "border-ok/30 bg-ok/5",
        tone === "info" && "border-line-strong bg-panel-2",
      )}
    >
      <Icon
        className={clsx(
          "mt-0.5 size-4 shrink-0",
          tone === "warn" && "text-warn",
          tone === "bad" && "text-bad",
          tone === "ok" && "text-ok",
          tone === "info" && "text-muted",
        )}
      />
      <div className="min-w-0 space-y-0.5">
        {title && <p className="font-medium text-fg">{title}</p>}
        {children && <div className="text-muted">{children}</div>}
      </div>
    </div>
  );
}

export function Spinner({ className }: { className?: string }) {
  return <Loader2 className={clsx("size-4 animate-spin text-muted", className)} />;
}

export function PageLoader() {
  return (
    <div className="flex h-40 items-center justify-center">
      <Spinner />
    </div>
  );
}

export function EmptyState({ icon, title, children, action }: { icon?: ReactNode; title: string; children?: ReactNode; action?: ReactNode }) {
  return (
    <div className="flex flex-col items-center justify-center gap-2 px-6 py-12 text-center">
      {icon && <div className="mb-1 rounded-lg border border-line-strong bg-panel-2 p-2.5 text-muted">{icon}</div>}
      <p className="text-sm font-medium text-fg">{title}</p>
      {children && <p className="max-w-sm text-[0.8125rem] text-muted">{children}</p>}
      {action && <div className="mt-3">{action}</div>}
    </div>
  );
}

export function PageHeader({ title, description, actions }: { title: ReactNode; description?: ReactNode; actions?: ReactNode }) {
  return (
    <div className="mb-6 flex flex-wrap items-end justify-between gap-4">
      <div className="min-w-0">
        <h1 className="text-xl font-medium tracking-tight text-fg">{title}</h1>
        {description && <p className="mt-1 text-[0.8125rem] text-muted">{description}</p>}
      </div>
      {actions && <div className="flex items-center gap-2">{actions}</div>}
    </div>
  );
}

export function Modal({ open, onOpenChange, title, description, children, footer, wide }: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: ReactNode;
  description?: ReactNode;
  children: ReactNode;
  footer?: ReactNode;
  wide?: boolean;
}) {
  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40 backdrop-blur-[2px]" />
        <Dialog.Content
          className={clsx(
            "fixed top-1/2 left-1/2 z-50 flex max-h-[90vh] w-[calc(100vw-32px)] -translate-x-1/2 -translate-y-1/2 flex-col rounded-lg border border-line-strong bg-panel shadow-2xl",
            wide ? "max-w-2xl" : "max-w-md",
          )}
        >
          <div className="flex items-start justify-between gap-4 border-b border-line px-5 py-4">
            <div>
              <Dialog.Title className="text-sm font-medium text-fg">{title}</Dialog.Title>
              {description ? (
                <Dialog.Description className="mt-1 text-xs text-muted">{description}</Dialog.Description>
              ) : (
                <Dialog.Description className="sr-only">{title}</Dialog.Description>
              )}
            </div>
            <Dialog.Close className="rounded p-1 text-muted hover:bg-hover hover:text-fg" aria-label="Chiudi">
              <X className="size-4" />
            </Dialog.Close>
          </div>
          <div className="overflow-y-auto px-5 py-4">{children}</div>
          {footer && <div className="flex justify-end gap-2 border-t border-line px-5 py-3">{footer}</div>}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

export function Segmented<T extends string>({ value, onChange, options }: {
  value: T;
  onChange: (value: T) => void;
  options: { value: T; label: ReactNode }[];
}) {
  return (
    <div className="inline-flex rounded-md border border-line-strong bg-field p-0.5">
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          onClick={() => onChange(o.value)}
          className={clsx(
            "inline-flex h-7 items-center gap-1.5 rounded px-3 text-xs font-medium transition-colors",
            value === o.value ? "bg-panel-2 text-fg shadow-sm" : "text-muted hover:text-fg",
          )}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}

export function CopyButton({ value }: { value: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      className="rounded p-1 text-faint hover:bg-hover hover:text-fg"
      aria-label="Copia"
      onClick={() => {
        navigator.clipboard.writeText(value).then(() => {
          setCopied(true);
          setTimeout(() => setCopied(false), 1500);
        });
      }}
    >
      {copied ? <Check className="size-3.5 text-ok" /> : <Copy className="size-3.5" />}
    </button>
  );
}

export function Mono({ children }: { children: ReactNode }) {
  return <code className="rounded bg-panel-2 px-1 py-px font-mono text-xs text-fg">{children}</code>;
}
