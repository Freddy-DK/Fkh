import { useState, useRef, useEffect } from 'react';

export interface MenuItem {
  label: string;
  onClick: () => void;
  disabled?: boolean;
  danger?: boolean;
  separator?: false;
}

export interface MenuSeparator {
  separator: true;
}

export type MenuEntry = MenuItem | MenuSeparator;

interface DropdownMenuProps {
  items: MenuEntry[];
  /** Button content — defaults to "⋮" */
  trigger?: React.ReactNode;
  /** Extra class on the trigger button */
  triggerClass?: string;
}

export function DropdownMenu({ items, trigger, triggerClass }: DropdownMenuProps) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  // Close on outside click
  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [open]);

  // Close on Escape
  useEffect(() => {
    if (!open) return;
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
    };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [open]);

  return (
    <div className="dropdown" ref={ref}>
      <button
        className={`dropdown-trigger ${triggerClass ?? ''}`}
        onClick={(e) => { e.stopPropagation(); setOpen(!open); }}
        aria-haspopup="true"
        aria-expanded={open}
      >
        {trigger ?? '⋮'}
      </button>
      {open && (
        <div className="dropdown-menu">
          {items.map((item, i) => {
            if (item.separator) {
              return <div key={i} className="dropdown-separator" />;
            }
            return (
              <button
                key={i}
                className={`dropdown-item ${item.danger ? 'dropdown-item-danger' : ''}`}
                onClick={(e) => {
                  e.stopPropagation();
                  setOpen(false);
                  item.onClick();
                }}
                disabled={item.disabled}
              >
                {item.label}
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}
