import { useEffect, useState } from 'react';
import { BookOpen, Disc3, Film, Tv } from 'lucide-react';
import clsx from 'clsx';

export interface LibrarySummary {
  id: number;
  slug: string;
  name: string;
  mediaType: string;
  rootFolderPath: string;
  plexSectionId: string | null;
  metadataWaterfall: string[];
  defaultProfileId: number | null;
  workCount: number;
  freeSpaceBytes?: number | null;
  totalSpaceBytes?: number | null;
  rootAccessible?: boolean;
}

export interface LanguagePolicyTierSummary {
  tier: number;
  label: string;
  audioContainsAll: string[];
  audioContainsAny: string[];
  subtitlesContainsAny: string[];
  satisfiesLanguageCutoff: boolean;
  scoreBonus: number;
}

export interface LanguagePolicySummary {
  id: number;
  slug: string;
  name: string;
  verificationMode: string;
  tiers: LanguagePolicyTierSummary[];
}

export interface MediaFileVerificationSummary {
  verdict: string;
  overallConfidence: number;
  audioMethod?: string | null;
  subMethod?: string | null;
  detectedAudioLanguages: string[];
  detectedSubLanguages: string[];
  durationMs: number;
  verifiedAt: string;
  failureReason?: string | null;
  failureCode?: string | null;
  matchedPolicyTier?: number | null;
  matchedPolicyTierLabel?: string | null;
  satisfiesLanguageCutoff?: boolean | null;
}

export interface MediaFileSummary {
  id: number;
  relativePath: string;
  sizeBytes: number;
  qualityName: string;
  detectedAudioLangs: string[];
  detectedSubLangs?: string[];
  verifiedStatus: string;
  inodeHardlinked: boolean;
  videoCodec?: string | null;
  hdrFormat?: string | null;
  sourceQualityName?: string | null;
  sourceVideoCodec?: string | null;
  sourceSizeBytes?: number | null;
  languageVerification?: MediaFileVerificationSummary | null;
  latestVerification?: MediaFileVerificationSummary | null;
}

export interface MediaUnitDetail {
  id: number;
  unitKind: string;
  unitNumber: number;
  absoluteNumber: number | null;
  title: string;
  airOrReleaseDate: string | null;
  monitored: boolean;
  requiredAudioLangs: string[];
  requiredSubLangs: string[];
  qualityCutoffMet: boolean;
  languageCutoffMet: boolean;
  files: MediaFileSummary[];
}

export interface MediaContainerDetail {
  id: number;
  containerKind: string;
  containerNumber: number;
  title: string;
  releaseDate: string | null;
  monitored: boolean;
  units: MediaUnitDetail[];
}

export interface MediaWorkSummary {
  id: number;
  libraryId: number;
  workKind: string;
  primaryProvider: string;
  episodeOrderingScheme: string;
  title: string;
  altTitles: string[];
  originalLanguage: string;
  year: number | null;
  status: string;
  overview: string;
  posterUrl: string | null;
  monitored: boolean;
  lockedFields: string[];
  externalIds: Record<string, string>;
  tags?: string[];
  containerCount: number;
  unitCount: number;
  fileCount: number;
  monitoredUnitCount?: number | null;
  downloadedUnitCount?: number | null;
  minimumAvailability?: string;
  qualityProfileId?: number | null;
  languagePolicyId?: number | null;
  addedAt?: string;
  updatedAt?: string;
}

export interface MediaWorkDetailTree {
  work: MediaWorkSummary;
  containers: MediaContainerDetail[];
}

export interface MetadataUnitSeed {
  unitKind: string;
  unitNumber: number;
  absoluteNumber: number | null;
  title: string;
  airOrReleaseDate: string | null;
}

export interface MetadataContainerSeed {
  containerKind: string;
  containerNumber: number;
  title: string;
  releaseDate: string | null;
  units: MetadataUnitSeed[];
}

export interface MetadataWorkCandidate {
  providerKey: string;
  providerItemId: string;
  vertical: string;
  title: string;
  altTitles: string[];
  originalLanguage: string;
  year: number | null;
  status: string;
  overview: string;
  posterUrl: string | null;
  externalIds: Record<string, string>;
  containers: MetadataContainerSeed[];
  genres?: string[] | null;
  classificationKind?: string | null;
  suggestedTags?: string[] | null;
  popularity?: number;
}

export interface CatalogAutoClassifyReportDto {
  evaluatedCount: number;
  updatedCount: number;
  countsByClassification: Record<string, number>;
  items: Array<{
    workId: number;
    title: string;
    workKind: string;
    classificationKind: string;
    addedTags: string[];
    allTags: string[];
  }>;
}

export interface WorkLanguageScanReportDto {
  workId: number;
  workTitle: string;
  filesScannedCount: number;
  autoAdoptedCount: number;
  missingRemovedCount: number;
  filesLanguageCheckedCount: number;
  verifiedPassCount: number;
  languageMismatchCount: number;
  durationMs: number;
  messages: string[];
}

export interface BulkWorkLanguageScanReportDto {
  targetedWorksCount: number;
  totalFilesScannedCount: number;
  totalAutoAdoptedCount: number;
  totalMissingRemovedCount: number;
  totalFilesLanguageCheckedCount: number;
  totalVerifiedPassCount: number;
  totalLanguageMismatchCount: number;
  durationMs: number;
  reports: WorkLanguageScanReportDto[];
}

export interface QualityProfileSummary {
  id: number;
  slug: string;
  name: string;
  mediaVertical: string;
  qualityCutoff: string;
  languagePolicyId?: number | null;
  languagePolicy?: LanguagePolicySummary | null;
}

export const COMMON_ISO_LANGUAGES: Array<{ code: string; label: string }> = [
  { code: 'ja', label: 'Japanese (ja)' },
  { code: 'de', label: 'German (de)' },
  { code: 'en', label: 'English (en)' },
  { code: 'ko', label: 'Korean (ko)' },
  { code: 'zh', label: 'Chinese (zh)' },
  { code: 'fr', label: 'French (fr)' },
  { code: 'es', label: 'Spanish (es)' },
  { code: 'it', label: 'Italian (it)' },
  { code: 'pt', label: 'Portuguese (pt)' },
  { code: 'ru', label: 'Russian (ru)' },
  { code: 'pl', label: 'Polish (pl)' },
  { code: 'nl', label: 'Dutch (nl)' },
  { code: 'sv', label: 'Swedish (sv)' },
  { code: 'da', label: 'Danish (da)' },
  { code: 'no', label: 'Norwegian (no)' },
  { code: 'tr', label: 'Turkish (tr)' },
];

export function formatLanguagePolicyExplainability(
  policy?: LanguagePolicySummary | null,
  originalLanguage: string = 'original',
): { cutoffSummary: string; fallbackSummary: string | null } {
  if (!policy || !policy.tiers || policy.tiers.length === 0) {
    return { cutoffSummary: 'German (DE)', fallbackSummary: null };
  }

  const origUpper = (originalLanguage || 'ORIG').toUpperCase();

  const formatLanguages = (langs: string[]) =>
    langs.map((l) => (l.toLowerCase() === 'original' ? origUpper : l.toUpperCase()));

  const formatTier = (tier: LanguagePolicyTierSummary) => {
    const parts: string[] = [];
    if (tier.audioContainsAll && tier.audioContainsAll.length > 0) {
      parts.push(formatLanguages(tier.audioContainsAll).join('+'));
    }
    if (tier.audioContainsAny && tier.audioContainsAny.length > 0) {
      parts.push(formatLanguages(tier.audioContainsAny).join('/'));
    }
    if (tier.subtitlesContainsAny && tier.subtitlesContainsAny.length > 0) {
      parts.push(`${formatLanguages(tier.subtitlesContainsAny).join('/')} Sub`);
    }
    return parts.length > 0 ? parts.join(' + ') : tier.label;
  };

  const cutoffTiers = policy.tiers.filter((t) => t.satisfiesLanguageCutoff);
  const fallbackTiers = policy.tiers.filter((t) => !t.satisfiesLanguageCutoff);

  const cutoffSummary = cutoffTiers.map(formatTier).join(', ') || 'Any Language';
  const fallbackSummary = fallbackTiers.length > 0 ? fallbackTiers.map(formatTier).join(', ') : null;

  return { cutoffSummary, fallbackSummary };
}

export interface AutoSearchResponse {
  workId: number;
  containerId?: number;
  unitId?: number;
  seasonNumber?: number;
  candidatesFound: number;
  grabbed: boolean;
  grabbedCount?: number;
  selectedRelease?: string;
  message: string;
}

export interface InteractiveSearchTarget {
  label: string;
  verticalOverride?: string;
  season: number | null;
  episode: number | null;
  unitId?: number | null;
}

export interface InteractiveReleaseRow {
  release: {
    guid?: string | null;
    indexerId?: number | null;
    indexerName?: string | null;
    protocol: string;
    mediaVertical: string;
    title: string;
    downloadUrl: string;
    magnetUrl: string | null;
    infoHash: string | null;
    sizeBytes?: number | null;
    seeders?: number | null;
    peers?: number | null;
    publishedAt?: string | null;
  };
  parsed: {
    mediaVertical: string;
    cleanTitle: string;
    seasonNumber?: number | null;
    episodeNumbers?: number[];
    absoluteEpisodeNumbers?: number[];
    isFullSeasonPack?: boolean;
    resolution: string | null;
    source: string | null;
    detectedLanguages: string[];
    isDualAudio: boolean;
  };
  ageHours?: number;
  indexerFlags?: string[];
  isBlocklisted?: boolean;
  approved?: boolean;
  decisionKind?: string;
  totalWeightedScore?: number;
  qualityName?: string;
  matchedCustomFormats?: string[];
  rejections?: string[];
}

export interface CalendarEntry {
  unitId: number;
  workId: number;
  workTitle: string;
  workKind: string;
  containerTitle: string;
  unitKind: string;
  unitNumber: number;
  unitTitle: string;
  airOrReleaseDate: string;
  monitored: boolean;
  hasFile: boolean;
}

export interface MediaRequestSummary {
  id: number;
  requesterUsername: string;
  libraryId: number;
  workId: number | null;
  mediaVertical: string;
  title: string;
  status: string;
  requestedAudioLang: string | null;
  requestedSubLang: string | null;
  createdAt: string;
}

export interface MetadataProviderDescriptor {
  key: string;
  displayName: string;
  isBuiltInArrDefault: boolean;
  requiresApiKey: boolean;
  hasApiKey?: boolean;
  configurationStatus?: string | null;
  supportedVerticals: string[];
  description: string;
}

export interface ProvidersCatalogResponse {
  builtInProviders: MetadataProviderDescriptor[];
  defaultWaterfall: string[];
  cacheStats?: {
    totalEntries: number;
    activeEntries: number;
    searchTtlHours: number;
    lookupTtlHours: number;
    nightlyRefreshHours: number;
  };
}

export type CatalogSubSection =
  | 'catalog'
  | 'add'
  | 'calendar'
  | 'requests'
  | 'media-management'
  | 'metadata';

export const LIBRARY_VERTICAL_BUBBLES: Array<{ id: string; label: string }> = [
  { id: 'movies', label: 'Movies' },
  { id: 'tv', label: 'TV Series' },
  { id: 'anime', label: 'Anime' },
  { id: 'music', label: 'Music' },
  { id: 'ebooks', label: 'Books' },
  { id: 'audiobooks', label: 'Audiobooks' },
  { id: 'comics', label: 'Comics' },
];

export type CatalogViewMode = 'posters' | 'overview' | 'table';
export type CatalogMonitorBubble = 'monitored' | 'unmonitored';
export type CatalogAvailabilityBubble = 'missing' | 'complete' | 'continuing';
export type CatalogSortField = 'title' | 'year' | 'missing' | 'added';

const ISO_INSTANT_RE =
  /^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?(Z|[+-]\d{2}:?\d{2})$/i;

function parseIsoInstantNanoseconds(raw: string): bigint | null {
  const m = ISO_INSTANT_RE.exec(raw);
  if (!m) return null;
  const year = Number(m[1]);
  const month = Number(m[2]);
  const day = Number(m[3]);
  const hour = Number(m[4]);
  const minute = Number(m[5]);
  const second = Number(m[6]);
  const msUtc = Date.UTC(year, month - 1, day, hour, minute, second);
  if (!Number.isFinite(msUtc)) return null;

  const fracNs = BigInt((m[7] ?? '').slice(0, 9).padEnd(9, '0'));
  const tz = m[8].toUpperCase();
  let offsetNs = 0n;
  if (tz !== 'Z') {
    const sign = tz[0] === '-' ? -1n : 1n;
    const digits = tz.slice(1).replace(':', '');
    const offH = BigInt(digits.slice(0, 2));
    const offM = BigInt(digits.slice(2, 4));
    offsetNs = sign * (offH * 3_600_000_000_000n + offM * 60_000_000_000n);
  }
  return BigInt(msUtc) * 1_000_000n + fracNs - offsetNs;
}

export function compareIsoInstants(aIso?: string | null, bIso?: string | null): number {
  const aTrim = (aIso ?? '').trim();
  const bTrim = (bIso ?? '').trim();
  if (!aTrim && !bTrim) return 0;
  if (!aTrim) return -1;
  if (!bTrim) return 1;

  const TemporalApi = (
    globalThis as {
      Temporal?: { Instant?: { from(s: string): unknown; compare(a: unknown, b: unknown): number } };
    }
  ).Temporal;
  if (TemporalApi?.Instant) {
    try {
      return TemporalApi.Instant.compare(
        TemporalApi.Instant.from(aTrim),
        TemporalApi.Instant.from(bTrim),
      );
    } catch {
      // Fall back to exact BigInt / Date.parse comparison below.
    }
  }

  const aNs = parseIsoInstantNanoseconds(aTrim);
  const bNs = parseIsoInstantNanoseconds(bTrim);
  if (aNs !== null && bNs !== null) {
    return aNs < bNs ? -1 : aNs > bNs ? 1 : 0;
  }

  const aMs = Date.parse(aTrim);
  const bMs = Date.parse(bTrim);
  const aValid = !Number.isNaN(aMs);
  const bValid = !Number.isNaN(bMs);
  if (!aValid && !bValid) return 0;
  if (!aValid) return -1;
  if (!bValid) return 1;
  return aMs < bMs ? -1 : aMs > bMs ? 1 : 0;
}

export function formatCatalogAddedDate(iso?: string | null): string {
  const trimmed = (iso ?? '').trim();
  if (!trimmed) return '—';
  const d = new Date(trimmed);
  if (Number.isNaN(d.getTime())) return '—';
  return d.toISOString().slice(0, 10);
}

export const VIEW_MODE_STORAGE_KEY = 'managerr.catalog.viewMode.v2';

export const ADD_VERTICAL_TABS: Array<{ id: string; label: string; placeholder: string }> = [
  {
    id: 'all',
    label: 'All Media',
    placeholder: 'eg. Dune: Part Two, Severance, Project Hail Mary, Meteora, imdb:tt15239678, tvdb:371980',
  },
  {
    id: 'movies',
    label: 'Movies',
    placeholder: 'eg. Dune: Part Two, Interstellar, Neon Horizon, imdb:tt15239678, tmdb:693134',
  },
  {
    id: 'tv',
    label: 'TV Series',
    placeholder: 'eg. Severance, Breaking Bad, Dark, tvdb:371980, imdb:tt11280740',
  },
  {
    id: 'anime',
    label: 'Anime',
    placeholder: 'eg. Frieren, Solo Leveling, Attack on Titan, anilist:154587',
  },
  {
    id: 'music',
    label: 'Music',
    placeholder: 'eg. Linkin Park, Meteora, Daft Punk, Random Access Memories',
  },
  {
    id: 'ebooks',
    label: 'Books',
    placeholder: 'eg. Project Hail Mary, Wind and Truth, Dune, isbn:9780593135204',
  },
  {
    id: 'audiobooks',
    label: 'Audiobooks',
    placeholder: 'eg. Project Hail Mary, Atomic Habits, asin:B08G9PRS1K',
  },
  {
    id: 'comics',
    label: 'Comics / Manga',
    placeholder: 'eg. Berserk, One Piece, Saga, Chainsaw Man',
  },
];

export const QUICK_SAMPLE_SEARCHES = [
  { label: 'Dune: Part Two', vertical: 'movies' },
  { label: 'Severance', vertical: 'tv' },
  { label: 'Frieren', vertical: 'anime' },
  { label: 'Project Hail Mary', vertical: 'ebooks' },
  { label: 'Meteora', vertical: 'music' },
  { label: 'Neon Horizon', vertical: 'movies' },
];

export function verticalNoun(vertical: string): string {
  switch (vertical.toLowerCase()) {
    case 'movies':
      return 'Movie';
    case 'tv':
      return 'Series';
    case 'anime':
      return 'Anime';
    case 'music':
      return 'Album';
    case 'ebooks':
      return 'Book';
    case 'audiobooks':
      return 'Audiobook';
    case 'comics':
      return 'Comic / Manga';
    default:
      return 'Media';
  }
}

export function defaultRootPathForVertical(vertical: string, existingLibs: LibrarySummary[]): string {
  const sampleRoot = existingLibs[0]?.rootFolderPath ?? '/media/movies';
  const basePrefix = sampleRoot.startsWith('/storage/media')
    ? '/storage/media'
    : sampleRoot.startsWith('/data/media')
      ? '/data/media'
      : '/media';
  const folder = vertical === 'ebooks' || vertical === 'audiobooks' ? 'books' : vertical;
  return `${basePrefix}/${folder}`;
}

export interface ClassificationBadgeInfo {
  key: string;
  label: string;
  badgeClass: string;
}

export function resolveClassificationBadge(
  item:
    | {
        workKind?: string;
        vertical?: string;
        tags?: string[] | null;
        suggestedTags?: string[] | null;
        classificationKind?: string | null;
      }
    | null
    | undefined,
): ClassificationBadgeInfo | null {
  if (!item) return null;
  const ck = item.classificationKind?.toLowerCase().trim();
  const rawTags = (item.tags ?? item.suggestedTags ?? []).map((t) => t.toLowerCase().trim());
  const tagsSet = new Set(rawTags);
  const kind = (item.workKind ?? item.vertical ?? '').toLowerCase().trim();
  const isMovie = kind === 'movie' || kind === 'movies';

  // 1. Anime Movie
  if (
    ck === 'anime-movie' ||
    tagsSet.has('anime-movie') ||
    (tagsSet.has('anime') && isMovie)
  ) {
    return {
      key: 'anime-movie',
      label: 'Anime Movie',
      badgeClass: 'bg-[#a855f7]/20 text-[#d8b4fe] border border-[#a855f7]/40',
    };
  }

  // 2. Anime Series
  if (
    ck === 'anime-series' ||
    tagsSet.has('anime-series') ||
    tagsSet.has('anime') ||
    (!isMovie && kind === 'anime')
  ) {
    return {
      key: 'anime-series',
      label: 'Anime Series',
      badgeClass: 'bg-[#ec4899]/20 text-[#f9a8d4] border border-[#ec4899]/40',
    };
  }

  // 3. Cartoon Movie
  if (
    ck === 'cartoon-movie' ||
    tagsSet.has('cartoon-movie') ||
    (tagsSet.has('cartoon') && isMovie)
  ) {
    return {
      key: 'cartoon-movie',
      label: 'Cartoon Movie',
      badgeClass: 'bg-[#f59e0b]/20 text-[#fcd34d] border border-[#f59e0b]/40',
    };
  }

  // 4. Cartoon Series / Cartoon
  if (
    ck === 'cartoon-series' ||
    tagsSet.has('cartoon-series') ||
    tagsSet.has('cartoon')
  ) {
    return {
      key: 'cartoon',
      label: 'Cartoon',
      badgeClass: 'bg-[#f97316]/20 text-[#fdba74] border border-[#f97316]/40',
    };
  }

  // 5. Live-Action Movie
  if (
    ck === 'live-action-movie' ||
    tagsSet.has('live-action-movie') ||
    (tagsSet.has('live-action') && isMovie)
  ) {
    return {
      key: 'live-action-movie',
      label: 'Live-Action Movie',
      badgeClass: 'bg-[#10b981]/20 text-[#6ee7b7] border border-[#10b981]/40',
    };
  }

  // 6. Live-Action TV
  if (
    ck === 'live-action-series' ||
    tagsSet.has('live-action-series') ||
    tagsSet.has('live-action')
  ) {
    return {
      key: 'live-action-series',
      label: 'Live-Action TV',
      badgeClass: 'bg-[#0ea5e9]/20 text-[#7dd3fc] border border-[#0ea5e9]/40',
    };
  }

  return null;
}

export function pickBestLibraryForCandidate(
  cand: MetadataWorkCandidate,
  libraries: LibrarySummary[],
  fallbackLibraryId?: number | string | null,
): LibrarySummary | undefined {
  if (!libraries || libraries.length === 0) return undefined;

  const fallbackLib =
    typeof fallbackLibraryId === 'number'
      ? libraries.find((l) => l.id === fallbackLibraryId)
      : typeof fallbackLibraryId === 'string' && Number.isFinite(Number(fallbackLibraryId))
        ? libraries.find((l) => l.id === Number(fallbackLibraryId))
        : undefined;

  const ck = cand.classificationKind?.toLowerCase().trim();
  const tags = (cand.suggestedTags ?? []).map((t) => t.toLowerCase().trim());
  const isAnimeMovie = ck === 'anime-movie' || tags.includes('anime-movie');
  const isAnimeSeries =
    ck === 'anime-series' || tags.includes('anime-series') || cand.vertical.toLowerCase() === 'anime';

  if (isAnimeMovie) {
    const animeMovieLib = libraries.find((l) => {
      const p = (l.rootFolderPath || '').toLowerCase();
      const n = (l.name || '').toLowerCase();
      return (
        p.includes('animefilme') ||
        p.includes('animemov') ||
        n.includes('animefilme') ||
        n.includes('animemov')
      );
    });
    if (animeMovieLib) return animeMovieLib;

    const movieLibs = libraries.filter((l) => l.mediaType.toLowerCase() === 'movies');
    if (movieLibs.length > 0) return movieLibs[0];

    return fallbackLib;
  }

  if (isAnimeSeries) {
    const animeLib = libraries.find(
      (l) => l.mediaType.toLowerCase() === 'anime' || (l.rootFolderPath || '').toLowerCase().includes('/anime'),
    );
    if (animeLib) return animeLib;

    const tvLib = libraries.find((l) => l.mediaType.toLowerCase() === 'tv');
    if (tvLib) return tvLib;

    return fallbackLib;
  }

  if (cand.vertical.toLowerCase() === 'movies') {
    const movieLibs = libraries.filter((l) => l.mediaType.toLowerCase() === 'movies');
    if (movieLibs.length > 0) {
      const nonAnimeMovieLib = movieLibs.find((l) => {
        const p = (l.rootFolderPath || '').toLowerCase();
        const n = (l.name || '').toLowerCase();
        return (
          !p.includes('animefilme') &&
          !p.includes('animemov') &&
          !n.includes('animefilme') &&
          !n.includes('animemov')
        );
      });
      return nonAnimeMovieLib ?? movieLibs[0];
    }
    return fallbackLib;
  }

  if (cand.vertical.toLowerCase() === 'tv') {
    const tvLib = libraries.find((l) => l.mediaType.toLowerCase() === 'tv');
    if (tvLib) return tvLib;
    return fallbackLib;
  }

  const matchingLib = libraries.find((l) => l.mediaType.toLowerCase() === cand.vertical.toLowerCase());
  return matchingLib ?? fallbackLib;
}

export function formatAgeHours(hours?: number | null): string {
  if (hours === undefined || hours === null) return '-';
  if (hours < 1) return '< 1h';
  if (hours < 24) return `${Math.round(hours)}h`;
  const days = Math.floor(hours / 24);
  return `${days}d`;
}

export interface HealthFilterMeta {
  title: string;
  description: string;
  resolveAction: string | null;
  resolveButtonLabel: string;
}

export function resolveHealthFilterMeta(normalizedHealth: string): HealthFilterMeta {
  switch (normalizedHealth) {
    case 'duplicates':
      return {
        title: 'Duplicate Unit Files',
        description: 'Showing library items flagged for duplicate media files attached to the same unit.',
        resolveAction: 'deduplicate_unit_files',
        resolveButtonLabel: 'Resolve All (Deduplicate Files)',
      };
    case 'non-canonical-quality':
      return {
        title: 'Non-Canonical Quality Labels',
        description: 'Showing library items with legacy or non-canonical quality labels.',
        resolveAction: 'canonicalize_qualities',
        resolveButtonLabel: 'Resolve All (Canonicalize Qualities)',
      };
    case 'wrong-kind':
      return {
        title: 'Mismatched Work Kind / Root Folder',
        description: 'Showing library works whose workKind does not match their assigned library vertical.',
        resolveAction: 'reclassify_mismatched_works',
        resolveButtonLabel: 'Resolve All (Reclassify Works)',
      };
    case 'missing-files':
      return {
        title: 'Missing Files on Disk',
        description: 'Showing library works with database file records that are missing on disk.',
        resolveAction: 'prune_missing_files',
        resolveButtonLabel: 'Resolve All (Prune Missing Files)',
      };
    default:
      return {
        title: `Health Filter: ${normalizedHealth}`,
        description: `Showing library works matching health filter '${normalizedHealth}'.`,
        resolveAction: null,
        resolveButtonLabel: 'Resolve All',
      };
  }
}

export function PosterArtwork({
  posterUrl,
  title,
  vertical,
  year,
  compact = false,
  className,
}: {
  posterUrl: string | null | undefined;
  title: string;
  vertical: string;
  year?: number | null;
  compact?: boolean;
  className?: string;
}) {
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    setFailed(false);
  }, [posterUrl]);

  const vLower = vertical.toLowerCase();
  const FallbackIcon =
    vLower === 'tv' || vLower === 'anime'
      ? Tv
      : vLower === 'music'
        ? Disc3
        : vLower === 'ebooks' || vLower === 'audiobooks' || vLower === 'comics'
          ? BookOpen
          : Film;

  const accentBarClass =
    vLower === 'movies'
      ? 'bg-[#ffc230]'
      : vLower === 'tv'
        ? 'bg-arr-accent'
        : vLower === 'anime'
          ? 'bg-[#9b6ef3]'
          : vLower === 'music'
            ? 'bg-[#27c24c]'
            : 'bg-arr-primary';

  if (posterUrl && !failed) {
    return (
      <img
        src={posterUrl}
        alt={title}
        loading="lazy"
        decoding="async"
        referrerPolicy="no-referrer"
        onError={() => setFailed(true)}
        className={clsx('object-cover bg-[#181a1d]', className)}
      />
    );
  }

  if (compact) {
    return (
      <div
        className={clsx(
          'bg-gradient-to-br from-[#232933] to-[#16191e] border border-[#363c44] flex items-center justify-center select-none relative overflow-hidden',
          className,
        )}
      >
        <div className={clsx('absolute inset-x-0 top-0 h-[2px]', accentBarClass)} />
        <FallbackIcon className="h-4 w-4 text-[#77818d] stroke-[1.6]" />
      </div>
    );
  }

  return (
    <div
      className={clsx(
        'bg-gradient-to-br from-[#232a35] via-[#1b2028] to-[#13161b] border border-[#343a42] flex flex-col justify-between p-3.5 text-left select-none relative overflow-hidden',
        className,
      )}
    >
      <div className={clsx('absolute inset-x-0 top-0 h-[3px]', accentBarClass)} />
      <div className="flex items-center justify-between text-[#7f8995]">
        <FallbackIcon className="h-5 w-5 stroke-[1.6]" />
        <span className="text-[9px] uppercase tracking-wider font-semibold opacity-80">
          {verticalNoun(vertical)}
        </span>
      </div>
      <div className="my-auto py-2">
        <div className="text-[13px] font-semibold text-[#d9dee4] line-clamp-4 leading-snug">{title}</div>
      </div>
      <div className="text-[10px] font-medium text-[#7b8490]">{year ?? 'No Poster'}</div>
    </div>
  );
}
