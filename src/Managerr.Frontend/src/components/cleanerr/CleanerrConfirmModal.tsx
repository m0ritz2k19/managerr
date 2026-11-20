import type { ReactNode } from 'react';
import { AlertTriangle, ShieldAlert } from 'lucide-react';
import { ArrButton, ArrModal } from '../arr/ArrPrimitives';

export interface CleanerrConfirmModalProps {
  title: ReactNode;
  description: ReactNode;
  confirmLabel: string;
  confirmVariant?: 'danger' | 'warning' | 'primary';
  busy?: boolean;
  onConfirm: () => void;
  onClose: () => void;
}

export function CleanerrConfirmModal({
  title,
  description,
  confirmLabel,
  confirmVariant = 'danger',
  busy = false,
  onConfirm,
  onClose,
}: CleanerrConfirmModalProps) {
  return (
    <ArrModal
      title={title}
      onClose={onClose}
      maxWidth="max-w-md"
      footer={
        <>
          <ArrButton
            variant="default"
            autoFocus
            disabled={busy}
            onClick={onClose}
          >
            Cancel
          </ArrButton>
          <ArrButton
            variant={confirmVariant}
            busy={busy}
            onClick={onConfirm}
          >
            {confirmLabel}
          </ArrButton>
        </>
      }
    >
      <div className="flex items-start gap-3">
        {confirmVariant === 'danger' ? (
          <AlertTriangle
            aria-hidden="true"
            className="h-5 w-5 shrink-0 mt-0.5 text-[var(--arr-danger)]"
          />
        ) : (
          <ShieldAlert
            aria-hidden="true"
            className="h-5 w-5 shrink-0 mt-0.5 text-[var(--arr-warning)]"
          />
        )}
        <div className="text-[13px] text-[var(--arr-text-dim)] leading-relaxed">
          {description}
        </div>
      </div>
    </ArrModal>
  );
}
