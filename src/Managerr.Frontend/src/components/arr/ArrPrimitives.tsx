import { useEffect, useId, useRef, type ButtonHTMLAttributes, type ReactNode } from 'react';
import { type LucideIcon, Loader2, Plus, X } from 'lucide-react';
import clsx from 'clsx';

export function ManagerrLogo({ size = 28 }: { size?: number }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 32 32"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
      aria-hidden="true"
      className="shrink-0"
    >
      <rect x="1" y="1" width="30" height="30" rx="5" fill="#1b1b1b" stroke="#393f45" strokeWidth="1.5" />
      <circle cx="16" cy="16" r="10.5" stroke="var(--arr-accent)" strokeWidth="2.2" strokeDasharray="46 18" />
      <circle cx="16" cy="16" r="6.5" stroke="var(--arr-logo-secondary)" strokeWidth="2" strokeDasharray="28 12" />
      <polygon points="14,12.2 20.5,16 14,19.8" fill="var(--arr-accent)" />
    </svg>
  );
}

export function PageToolbar({
  left,
  right,
}: {
  left?: ReactNode;
  right?: ReactNode;
}) {
  return (
    <div className="flex flex-wrap items-center justify-between min-h-[60px] px-4 lg:px-5 bg-arr-toolbar border-b border-arr-card-border text-[#e1e2e3] select-none shrink-0 gap-2">
      <div className="flex flex-wrap items-center gap-0.5">{left}</div>
      {right && <div className="flex flex-wrap items-center gap-0.5 ml-auto">{right}</div>}
    </div>
  );
}

export function PageToolbarSeparator() {
  return <div className="h-[38px] w-px bg-white/12 mx-2.5 hidden sm:block" />;
}

export function PageToolbarButton({
  icon: Icon,
  label,
  onClick,
  disabled,
  spinning,
  active,
  title,
}: {
  icon: LucideIcon;
  label: string;
  onClick?: () => void;
  disabled?: boolean;
  spinning?: boolean;
  active?: boolean;
  title?: string;
}) {
  return (
    <button
      type="button"
      title={title ?? label}
      disabled={disabled}
      onClick={onClick}
      className={clsx(
        'group inline-flex flex-col items-center justify-center min-w-[60px] px-2 py-1 text-center transition-colors cursor-pointer disabled:opacity-45 disabled:pointer-events-none',
        active ? 'text-arr-accent' : 'text-[#e1e2e3] hover:text-arr-accent',
      )}
    >
      <Icon className={clsx('h-[20px] w-[20px] mb-1 transition-colors', spinning && 'animate-spin')} />
      <span
        className={clsx(
          'text-[11px] leading-[12px] tracking-tight whitespace-nowrap',
          active ? 'text-arr-accent font-medium' : 'text-[#e1e2e3] group-hover:text-arr-accent',
        )}
      >
        {label}
      </span>
    </button>
  );
}

export function PageContentBody({
  children,
  className,
}: {
  children: ReactNode;
  className?: string;
}) {
  return <div className={clsx('flex-1 p-5 lg:p-6 overflow-x-hidden', className)}>{children}</div>;
}

export function FieldSet({
  legend,
  action,
  children,
  className,
}: {
  legend: ReactNode;
  action?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  return (
    <fieldset className={clsx('m-0 mb-7 p-0 min-w-0 border-0', className)}>
      <div className="flex items-end justify-between border-b border-[#444444] pb-1.5 mb-5 gap-3">
        <legend className="p-0 text-[20px] font-normal text-[#e1e2e3] leading-snug">{legend}</legend>
        {action && <div className="shrink-0">{action}</div>}
      </div>
      {children}
    </fieldset>
  );
}

export function FormGroup({
  label,
  helpText,
  htmlFor,
  required,
  children,
}: {
  label: ReactNode;
  helpText?: ReactNode;
  htmlFor?: string;
  required?: boolean;
  children: ReactNode;
}) {
  return (
    <div className="grid grid-cols-1 md:grid-cols-[220px_minmax(0,440px)] items-start gap-x-5 gap-y-1 mb-4">
      <label
        htmlFor={htmlFor}
        className="md:text-right md:pt-1.5 text-[13px] font-bold text-[#e1e2e3] leading-snug"
      >
        {label}
        {required && <span className="text-[#f05050] ml-1">*</span>}
      </label>
      <div>
        {children}
        {helpText && <p className="mt-1 text-[12px] text-[#909293] leading-snug">{helpText}</p>}
      </div>
    </div>
  );
}

export type ArrLabelKind =
  | 'default'
  | 'primary'
  | 'info'
  | 'success'
  | 'torrent'
  | 'usenet'
  | 'warning'
  | 'danger'
  | 'purple'
  | 'inverse';

const LABEL_CLASSES: Record<ArrLabelKind, string> = {
  default: 'bg-[#494949] text-[#eeeeee] border-[#595959]',
  primary: 'bg-arr-primary text-white border-arr-primary-border',
  info: 'bg-[#23b7e5] text-white border-[#1db0de]',
  success: 'bg-[#27c24c] text-white border-[#26be4a]',
  torrent: 'bg-[#00853d] text-white border-[#009946]',
  usenet: 'bg-[#17b1d9] text-white border-[#1cc1ec]',
  warning: 'bg-[#ffa500] text-[#191919] border-[#ffae1a]',
  danger: 'bg-[#f05050] text-white border-[#f04b4b]',
  purple: 'bg-[#7a43b6] text-white border-[#8a52c7]',
  inverse: 'bg-[#dddddd] text-[#222222] border-[#dddddd]',
};

export function ArrLabel({
  kind = 'default',
  children,
  className,
  title,
}: {
  kind?: ArrLabelKind;
  children: ReactNode;
  className?: string;
  title?: string;
}) {
  return (
    <span
      title={title}
      className={clsx(
        'inline-flex items-center gap-1 px-1.5 py-[2px] text-[11px] font-semibold leading-tight rounded-[3px] border whitespace-nowrap',
        LABEL_CLASSES[kind],
        className,
      )}
    >
      {children}
    </span>
  );
}

export type ArrButtonVariant = 'default' | 'primary' | 'success' | 'warning' | 'danger' | 'plex';

const BTN_CLASSES: Record<ArrButtonVariant, string> = {
  default: 'bg-arr-card hover:bg-arr-card/80 border-arr-card-border text-[#eeeeee]',
  primary: 'bg-arr-primary hover:bg-arr-primary-hover border-arr-primary-border text-white',
  success: 'bg-[#27c24c] hover:bg-[#24b145] border-[#24b145] text-white',
  warning: 'bg-[#ff902b] hover:bg-[#ff8517] border-[#ff8517] text-[#191919] font-semibold',
  danger: 'bg-[#f05050] hover:bg-[#ee3d3d] border-[#ee3d3d] text-white',
  plex: 'bg-[#e5a00d] hover:bg-[#f0ad1f] border-[#d39209] text-[#191919] font-semibold',
};

export function ArrButton({
  variant = 'default',
  busy,
  children,
  className,
  disabled,
  type = 'button',
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: ArrButtonVariant;
  busy?: boolean;
}) {
  return (
    <button
      type={type}
      disabled={disabled || busy}
      className={clsx(
        'inline-flex items-center justify-center gap-1.5 h-[34px] px-3.5 text-[13px] font-medium rounded-[4px] border transition-colors cursor-pointer disabled:opacity-50 disabled:pointer-events-none',
        BTN_CLASSES[variant],
        className,
      )}
      {...rest}
    >
      {busy && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
      {children}
    </button>
  );
}

export function ArrAlert({
  kind = 'info',
  children,
  className,
}: {
  kind?: 'info' | 'success' | 'warning' | 'danger';
  children: ReactNode;
  className?: string;
}) {
  const styles = {
    info: 'border-[#31708f] bg-[#31708f]/20 text-[#d9edf7]',
    success: 'border-[#3c763d] bg-[#3c763d]/25 text-[#dff0d8]',
    warning: 'border-[#8a6d3b] bg-[#8a6d3b]/25 text-[#fcf8e3]',
    danger: 'border-[#a94442] bg-[#a94442]/25 text-[#f2dede]',
  }[kind];

  return <div className={clsx('px-3.5 py-2.5 rounded-[4px] border text-[13px]', styles, className)}>{children}</div>;
}

export function ArrSettingsCard({
  title,
  topRight,
  onClick,
  children,
  footer,
}: {
  title: ReactNode;
  topRight?: ReactNode;
  onClick?: () => void;
  children: ReactNode;
  footer?: ReactNode;
}) {
  return (
    <div
      onClick={onClick}
      className={clsx(
        'arr-card w-full sm:w-[290px] min-h-[156px] p-4 flex flex-col justify-between',
        onClick && 'arr-card-interactive',
      )}
    >
      <div>
        <div className="flex items-start justify-between gap-2 mb-3">
          <div className="text-[20px] font-light text-[#eeeeee] leading-tight truncate">{title}</div>
          {topRight && <div className="shrink-0">{topRight}</div>}
        </div>
        <div className="space-y-2 text-[12px] text-[#b8b8b8]">{children}</div>
      </div>
      {footer && <div className="pt-3 mt-3 border-t border-[#424242] flex items-center justify-between">{footer}</div>}
    </div>
  );
}

export function ArrAddCard({
  onClick,
  label = 'Add',
}: {
  onClick: () => void;
  label?: string;
}) {
  return (
    <button
      type="button"
      title={label}
      onClick={onClick}
      className="arr-card arr-card-interactive w-full sm:w-[290px] min-h-[156px] p-4 flex items-stretch cursor-pointer group"
    >
      <div className="flex-1 bg-arr-card/60 border border-arr-card-border rounded-[3px] flex flex-col items-center justify-center text-[#888888] group-hover:text-arr-accent group-hover:border-arr-accent/60 transition-colors">
        <Plus className="h-12 w-12 stroke-[1.5]" />
      </div>
    </button>
  );
}

export function SmartBreakText({
  text,
  className,
}: {
  text: string;
  className?: string;
}) {
  const parts = text.split(/([.\-_/\\]+)/);
  return (
    <span className={clsx('break-normal [overflow-wrap:anywhere]', className)}>
      {parts.map((part, idx) =>
        idx % 2 === 1 ? (
          <span key={idx}>
            {part}
            <wbr />
          </span>
        ) : (
          <span key={idx}>{part}</span>
        ),
      )}
    </span>
  );
}

export function compactTraitLabel(raw: string): string {
  let s = raw.trim();
  const cfMatch = /^CF\s+'([^']+)'\s*\(([^)]+)\)$/i.exec(s);
  if (cfMatch) {
    return `${cfMatch[1]} ${cfMatch[2]}`;
  }
  const rpMatch = /^ReleaseProfile\s+'[^']+':\s*'([^']+)'\s*\(([^)]+)\)$/i.exec(s);
  if (rpMatch) {
    return `${rpMatch[1]} ${rpMatch[2]}`;
  }
  if (s.includes(':')) {
    s = s.slice(s.indexOf(':') + 1).trim();
  }
  s = s
    .replace(/\bVideo Codec\b/gi, '')
    .replace(/\bAudio Codec\b/gi, '')
    .replace(/\s+\(([-+]?\d+)\)$/, ' $1')
    .replace(/\s{2,}/g, ' ')
    .trim();
  return s.length > 26 ? `${s.slice(0, 24)}…` : s;
}

export function ArrModal({
  title,
  onClose,
  footer,
  maxWidth = 'max-w-2xl',
  bodyClassName,
  children,
}: {
  title: ReactNode;
  onClose: () => void;
  footer?: ReactNode;
  maxWidth?: string;
  bodyClassName?: string;
  children: ReactNode;
}) {
  const titleId = useId();
  const dialogRef = useRef<HTMLDialogElement | null>(null);
  const pointerDownOutsideRef = useRef(false);

  useEffect(() => {
    const dialog = dialogRef.current;
    if (dialog && !dialog.open) {
      dialog.showModal();
    }
  }, []);

  const isOutsideBoundingRect = (clientX: number, clientY: number, el: HTMLElement) => {
    const rect = el.getBoundingClientRect();
    if (rect.width === 0 && rect.height === 0) return false;
    return (
      clientX < rect.left ||
      clientX > rect.right ||
      clientY < rect.top ||
      clientY > rect.bottom
    );
  };

  return (
    <dialog
      ref={dialogRef}
      aria-labelledby={titleId}
      onClose={(e) => {
        e.stopPropagation();
        onClose();
      }}
      onCancel={(e) => {
        e.stopPropagation();
      }}
      {...({ closedby: 'any' } as Record<string, string>)}
      onPointerDown={(e) => {
        if (e.target === e.currentTarget) {
          pointerDownOutsideRef.current = isOutsideBoundingRect(e.clientX, e.clientY, e.currentTarget);
        } else {
          pointerDownOutsideRef.current = false;
        }
      }}
      onPointerUp={(e) => {
        const supportsClosedBy =
          typeof HTMLDialogElement !== 'undefined' && 'closedBy' in HTMLDialogElement.prototype;
        if (!supportsClosedBy) {
          if (pointerDownOutsideRef.current && e.target === e.currentTarget) {
            if (isOutsideBoundingRect(e.clientX, e.clientY, e.currentTarget)) {
              onClose();
            }
          }
        }
        pointerDownOutsideRef.current = false;
      }}
      className={clsx(
        'm-auto p-0 bg-transparent border-0 w-[calc(100%-1rem)] sm:w-[calc(100%-2rem)] max-h-[92vh] text-[#e1e2e3] backdrop:bg-black/70 open:flex open:flex-col',
        maxWidth,
      )}
    >
      <div
        className="w-full bg-arr-modal border border-arr-card-border rounded-[4px] shadow-[0_8px_28px_rgba(0,0,0,0.85)] flex flex-col max-h-[92vh] overflow-hidden"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between px-5 py-3 border-b border-arr-card-border shrink-0 gap-3">
          <h2 id={titleId} className="text-[17px] font-normal text-[#e1e2e3] truncate">{title}</h2>
          <button
            type="button"
            onClick={onClose}
            className="text-[#888888] hover:text-[#eeeeee] p-1 cursor-pointer shrink-0"
            aria-label="Close"
          >
            <X className="h-5 w-5" />
          </button>
        </div>
        <div className={bodyClassName ?? 'p-5 overflow-y-auto flex-1'}>{children}</div>
        {footer && (
          <div className="flex items-center justify-end gap-2 px-5 py-2.5 border-t border-arr-card-border bg-arr-toolbar shrink-0">
            {footer}
          </div>
        )}
      </div>
    </dialog>
  );
}

export type FilterBubbleTone = 'cyan' | 'green' | 'amber' | 'rose' | 'purple' | 'blue';

const BUBBLE_ACTIVE_TONES: Record<FilterBubbleTone, string> = {
  cyan: 'bg-arr-accent/20 border-arr-accent text-white shadow-[0_0_0_1px_var(--arr-accent)]',
  green: 'bg-[#27c24c]/20 border-[#27c24c] text-[#e6ffec] shadow-[0_0_0_1px_rgba(39,194,76,0.25)]',
  amber: 'bg-[#ffa726]/20 border-[#ffa726] text-[#fff4e0] shadow-[0_0_0_1px_rgba(255,167,38,0.25)]',
  rose: 'bg-[#f05050]/20 border-[#f05050] text-[#ffe8e8] shadow-[0_0_0_1px_rgba(240,80,80,0.25)]',
  purple: 'bg-[#9b6ef3]/20 border-[#9b6ef3] text-[#f2ebff] shadow-[0_0_0_1px_rgba(155,110,243,0.25)]',
  blue: 'bg-arr-primary/25 border-arr-primary-border text-white shadow-[0_0_0_1px_var(--arr-primary-border)]',
};

const BUBBLE_DOT_TONES: Record<FilterBubbleTone, string> = {
  cyan: 'bg-arr-accent',
  green: 'bg-[#27c24c]',
  amber: 'bg-[#ffa726]',
  rose: 'bg-[#f05050]',
  purple: 'bg-[#9b6ef3]',
  blue: 'bg-arr-accent',
};

export function FilterBubble({
  label,
  active,
  onClick,
  count,
  icon: Icon,
  tone = 'cyan',
  title,
  groupLabelId,
}: {
  label: ReactNode;
  active: boolean;
  onClick: () => void;
  count?: number;
  icon?: LucideIcon;
  tone?: FilterBubbleTone;
  title?: string;
  groupLabelId?: string;
}) {
  const contentId = useId();
  return (
    <button
      type="button"
      onClick={onClick}
      aria-pressed={active}
      aria-labelledby={groupLabelId ? `${contentId} ${groupLabelId}` : undefined}
      title={title}
      className={clsx(
        'inline-flex items-center gap-1.5 h-[28px] px-3 rounded-full border text-[12px] font-medium transition-all select-none cursor-pointer whitespace-nowrap',
        active
          ? BUBBLE_ACTIVE_TONES[tone]
          : 'bg-[#25282c] border-[#3a4046] text-[#a9afb6] hover:border-[#59626b] hover:text-[#eeeeee] hover:bg-[#2c3035]',
      )}
    >
      {Icon ? (
        <Icon aria-hidden="true" className={clsx('h-3.5 w-3.5 shrink-0', active ? 'text-current' : 'text-[#848b94]')} />
      ) : (
        <span
          aria-hidden="true"
          className={clsx(
            'h-2 w-2 rounded-full shrink-0 transition-colors',
            active ? BUBBLE_DOT_TONES[tone] : 'bg-[#555b62]',
          )}
        />
      )}
      <span id={groupLabelId ? contentId : undefined} className="inline-flex items-center gap-1.5">
        <span>{label}</span>
        {count !== undefined && (
          <span
            className={clsx(
              'px-1.5 py-[1px] rounded-full text-[10px] font-semibold leading-none',
              active ? 'bg-black/35 text-white' : 'bg-[#1c1e21] text-[#888f98]',
            )}
          >
            {count}
          </span>
        )}
      </span>
    </button>
  );
}

export function FilterBubbleGroup({
  label,
  labelId,
  children,
  className,
}: {
  label: string;
  labelId: string;
  children: ReactNode;
  className?: string;
}) {
  return (
    <div
      role="group"
      aria-labelledby={labelId}
      className={clsx(
        'inline-flex flex-wrap items-center gap-1.5 rounded-[6px] bg-black/20 border border-[#323840] px-2.5 py-1',
        className,
      )}
    >
      <span
        id={labelId}
        className="text-[10px] font-bold uppercase tracking-wider text-[#8d959e] border-r border-[#323840] pr-2 mr-0.5 select-none"
      >
        {label}
      </span>
      {children}
    </div>
  );
}

export function toggleFilterSetValue<T>(prev: Set<T>, value: T): Set<T> {
  const next = new Set(prev);
  if (next.has(value)) {
    next.delete(value);
  } else {
    next.add(value);
  }
  return next;
}


