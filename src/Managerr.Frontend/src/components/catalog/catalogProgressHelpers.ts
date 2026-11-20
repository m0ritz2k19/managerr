import type { MediaContainerDetail, MediaUnitDetail, MediaWorkSummary } from './catalogTypesAndHelpers';

export interface WorkDownloadProgress {
  downloadedUnits: number;
  targetUnits: number;
  totalUnits: number;
  missingMonitoredUnits: number;
  isComplete: boolean;
  isPartial: boolean;
  hasMissingMonitored: boolean;
  percent: number;
}

export function resolveWorkDownloadProgress(w: MediaWorkSummary): WorkDownloadProgress {
  const totalUnits = Math.max(0, w.unitCount);
  const targetUnits =
    typeof w.monitoredUnitCount === 'number' ? Math.max(0, w.monitoredUnitCount) : totalUnits;
  const downloadedUnits =
    typeof w.downloadedUnitCount === 'number'
      ? Math.max(0, w.downloadedUnitCount)
      : Math.max(0, Math.min(w.fileCount, targetUnits > 0 ? targetUnits : Math.max(w.fileCount, totalUnits)));

  const missingMonitoredUnits = Math.max(0, targetUnits - downloadedUnits);
  const isComplete = targetUnits > 0 && downloadedUnits >= targetUnits;
  const hasMissingMonitored = missingMonitoredUnits > 0;
  const isPartial = !isComplete && downloadedUnits > 0 && hasMissingMonitored;
  const percent = targetUnits > 0 ? Math.min(100, Math.round((downloadedUnits / targetUnits) * 100)) : 0;

  return {
    downloadedUnits,
    targetUnits,
    totalUnits,
    missingMonitoredUnits,
    isComplete,
    isPartial,
    hasMissingMonitored,
    percent,
  };
}

export function isEpisodeUnitAiredOrReleased(
  unit: MediaUnitDetail,
  workHasAnyDatedUnits: boolean,
  todayIso: string,
): boolean {
  if (unit.unitKind.toLowerCase() !== 'episode') {
    return true;
  }
  const rawDate = (unit.airOrReleaseDate ?? '').trim();
  if (rawDate.length > 0) {
    return rawDate.slice(0, 10) <= todayIso;
  }
  if (unit.title.trim().toUpperCase() === 'TBA') {
    return false;
  }
  return !workHasAnyDatedUnits;
}

export interface ContainerDownloadProgress {
  downloadedCount: number;
  targetUnitCount: number;
  totalUnitCount: number;
  monitoredMissingCount: number;
  badgeKind: 'success' | 'warning' | 'danger' | 'default';
}

export function resolveContainerDownloadProgress(
  container: MediaContainerDetail,
  allContainers: readonly MediaContainerDetail[],
  todayIso: string,
): ContainerDownloadProgress {
  const workHasAnyDatedUnits = allContainers.some((c) =>
    c.units.some((u) => Boolean(u.airOrReleaseDate && u.airOrReleaseDate.trim().length > 0)),
  );

  let downloadedCount = 0;
  let targetUnitCount = 0;

  for (const unit of container.units) {
    const hasFile = unit.files.length > 0;
    if (hasFile) {
      downloadedCount++;
      targetUnitCount++;
      continue;
    }
    if (
      container.monitored &&
      unit.monitored &&
      isEpisodeUnitAiredOrReleased(unit, workHasAnyDatedUnits, todayIso)
    ) {
      targetUnitCount++;
    }
  }

  const monitoredMissingCount = Math.max(0, targetUnitCount - downloadedCount);
  const badgeKind: 'success' | 'warning' | 'danger' | 'default' =
    targetUnitCount > 0 && downloadedCount >= targetUnitCount
      ? container.monitored
        ? 'success'
        : 'default'
      : downloadedCount > 0 && monitoredMissingCount > 0
        ? 'warning'
        : monitoredMissingCount > 0
          ? 'danger'
          : 'default';

  return {
    downloadedCount,
    targetUnitCount,
    totalUnitCount: container.units.length,
    monitoredMissingCount,
    badgeKind,
  };
}
