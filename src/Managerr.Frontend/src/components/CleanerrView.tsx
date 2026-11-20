import { useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import {
  Bot,
  Clock,
  Film,
  LayoutDashboard,
  Shield,
  Workflow,
  type LucideIcon,
} from 'lucide-react';
import { apiFetch } from '../lib/api';
import { ArrAlert, ArrLabel } from './arr/ArrPrimitives';
import { CleanerrDiscordAndSettingsSection } from './cleanerr/CleanerrDiscordAndSettingsSection';
import { CleanerrMediaAndExclusionsSection } from './cleanerr/CleanerrMediaAndExclusionsSection';
import { CleanerrOverviewSection } from './cleanerr/CleanerrOverviewSection';
import { CleanerrQueueSection } from './cleanerr/CleanerrQueueSection';
import { CleanerrStudioSection } from './cleanerr/CleanerrStudioSection';
import type { CleanerrStatusResponse } from './cleanerr/cleanerrTypes';

export type CleanerrSection =
  | 'overview'
  | 'studio'
  | 'queue'
  | 'media'
  | 'exclusions'
  | 'discord';

interface CleanerrNavItem {
  id: CleanerrSection;
  label: string;
  path: string;
  icon: LucideIcon;
}

const CLEANERR_NAV_ITEMS: CleanerrNavItem[] = [
  { id: 'overview', label: 'Overview', path: '/cleanerr', icon: LayoutDashboard },
  { id: 'studio', label: 'Studio', path: '/cleanerr/studio', icon: Workflow },
  { id: 'queue', label: 'Leaving Soon', path: '/cleanerr/queue', icon: Clock },
  { id: 'media', label: 'Media Catalog', path: '/cleanerr/media', icon: Film },
  { id: 'exclusions', label: 'Exclusions', path: '/cleanerr/exclusions', icon: Shield },
  { id: 'discord', label: 'Discord & Settings', path: '/cleanerr/discord', icon: Bot },
];

export function CleanerrView({ section }: { section?: CleanerrSection }) {
  const navigate = useNavigate();
  const [localSection, setLocalSection] = useState<CleanerrSection>(
    section ?? 'overview',
  );
  const activeSection: CleanerrSection = section ?? localSection;

  const statusQuery = useQuery({
    queryKey: ['cleanerr-status'],
    queryFn: () => apiFetch<CleanerrStatusResponse>('/api/v1/cleanerr/status'),
  });

  const status = statusQuery.data;

  const handleNavigateSection = (target: CleanerrSection) => {
    setLocalSection(target);
    const match = CLEANERR_NAV_ITEMS.find((item) => item.id === target);
    if (match) {
      navigate(match.path);
    }
  };

  return (
    <div className="flex flex-col flex-1 min-w-0">
      {/* Sub-navigation bar with underline indicator and RBAC badges */}
      <div className="flex flex-wrap items-center justify-between gap-3 px-4 lg:px-5 bg-[var(--arr-heading-bg)] border-b border-[var(--arr-card-border)] shrink-0">
        <nav
          aria-label="Cleanerr sections"
          className="flex items-center gap-1 overflow-x-auto"
        >
          {CLEANERR_NAV_ITEMS.map((item) => {
            const Icon = item.icon;
            const isActive = activeSection === item.id;
            const badgeCount =
              item.id === 'queue' && typeof status?.stagedCount === 'number'
                ? status.stagedCount
                : null;

            return (
              <Link
                key={item.id}
                to={item.path}
                aria-current={isActive ? 'page' : undefined}
                onClick={() => setLocalSection(item.id)}
                className={`relative inline-flex items-center gap-1.5 px-3 py-2.5 text-[13px] font-medium whitespace-nowrap transition-colors border-b-[3px] ${
                  isActive
                    ? 'text-[var(--arr-accent)] border-[var(--arr-accent)]'
                    : 'text-[var(--arr-text-dim)] hover:text-[var(--arr-text)] border-transparent'
                }`}
              >
                <Icon aria-hidden="true" className="h-4 w-4 shrink-0" />
                <span>{item.label}</span>
                {badgeCount !== null && badgeCount > 0 && (
                  <span className="px-1.5 py-[1px] rounded-full text-[10px] font-bold bg-[#ffa726] text-[#191919] leading-none">
                    {badgeCount}
                  </span>
                )}
              </Link>
            );
          })}
        </nav>

        {/* RBAC Scope Badges */}
        {status && (
          <div
            className="flex flex-wrap items-center gap-1.5 py-1.5"
            aria-label="Cleanerr permissions"
          >
            {status.canWriteWorkflows && (
              <ArrLabel kind="success">Workflows: Write</ArrLabel>
            )}
            {status.canExecute && (
              <ArrLabel kind="warning">Execute</ArrLabel>
            )}
            {status.canRescue && (
              <ArrLabel kind="info">Rescue</ArrLabel>
            )}
            {status.canExclude && (
              <ArrLabel kind="primary">
                {status.cleanerrExclusionLimit !== null
                  ? `Exclude Limit: ${status.cleanerrExclusionLimit}`
                  : 'Exclude: Unlimited'}
              </ArrLabel>
            )}
            {!status.canWriteWorkflows &&
              !status.canExecute &&
              !status.canRescue &&
              !status.canExclude && (
                <ArrLabel kind="default">Read Only</ArrLabel>
              )}
          </div>
        )}
      </div>

      {statusQuery.isError && (
        <div className="px-5 pt-4">
          <ArrAlert kind="danger">
            {(statusQuery.error as Error)?.message ??
              'Failed to load Cleanerr module status.'}
          </ArrAlert>
        </div>
      )}

      {activeSection === 'overview' && (
        <CleanerrOverviewSection
          status={status}
          onNavigateSection={handleNavigateSection}
        />
      )}

      {activeSection === 'studio' && <CleanerrStudioSection status={status} />}

      {activeSection === 'queue' && <CleanerrQueueSection status={status} />}

      {(activeSection === 'media' || activeSection === 'exclusions') && (
        <CleanerrMediaAndExclusionsSection
          key={activeSection}
          status={status}
          initialSubTab={
            activeSection === 'exclusions' ? 'exclusions' : 'browser'
          }
        />
      )}

      {activeSection === 'discord' && (
        <CleanerrDiscordAndSettingsSection status={status} />
      )}
    </div>
  );
}
