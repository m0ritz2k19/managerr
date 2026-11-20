export interface MaskedSecretDto {
  isSet: boolean;
  maskedHint: string | null;
}

export interface IndexerSummaryDto {
  id: number;
  name: string;
  implementation: string;
  definitionId: string | null;
  protocol: string;
  baseUrl: string;
  apiKeySecret?: MaskedSecretDto;
  categories: number[];
  tags: string[];
  priority: number;
  rssEnabled: boolean;
  interactiveSearchEnabled: boolean;
  autoSearchEnabled: boolean;
  requestDelayMs: number;
  isHealthy: boolean;
  consecutiveFailures: number;
  lastError: string | null;
}

export interface DriverDto {
  implementation: string;
  displayName: string;
  protocol: string;
  defaultPort: number;
  authMode: string;
  description: string;
}

export interface DownloadClientDto {
  id: number;
  name: string;
  implementation: string;
  protocol: string;
  host: string;
  port: number;
  useSsl?: boolean;
  username?: string | null;
  passwordSecret?: MaskedSecretDto;
  apiKeySecret?: MaskedSecretDto;
  categoryMappings: Record<string, string>;
  priority?: number;
  enabled?: boolean;
}

export interface SearchResultRow {
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
    publishedAt?: string | null;
    seeders?: number | null;
    peers?: number | null;
  };
  parsed: {
    mediaVertical: string;
    cleanTitle: string;
    resolution: string | null;
    source: string | null;
    detectedLanguages: string[];
    isDualAudio: boolean;
  };
  isBlocklisted?: boolean;
  approved?: boolean;
  rejections?: string[];
}

export interface ActiveQueueItemDto {
  clientId: number;
  clientName: string;
  protocol: string;
  externalId: string;
  infoHash: string | null;
  title: string;
  category: string;
  mediaVertical: string;
  status: string;
  totalBytes: number;
  remainingBytes: number;
  outputPath: string;
  containedFiles: string[];
  isStalled: boolean;
  isMetadataStuck: boolean;
  errorMessage: string | null;
}

export interface ProcessedImportRow {
  id: number;
  downloadClientId: number;
  externalDownloadId: string;
  title: string;
  verdict: string;
  destinationPath: string | null;
  hardlinked: boolean;
  message: string;
  processedAt: string;
}

export interface ProcessCompletedReport {
  evaluatedCount: number;
  importedCount: number;
  quarantinedCount: number;
  rejectedCount: number;
}

export interface IndexerFormState {
  editingId: number | null;
  hasSavedApiKey: boolean;
  name: string;
  implementation: 'newznab' | 'torznab' | 'cardigann';
  definitionId: string | null;
  protocol: 'usenet' | 'torrent';
  baseUrl: string;
  apiKey: string;
  categories: number[];
  priority: number;
  rssEnabled: boolean;
  autoSearchEnabled: boolean;
  interactiveSearchEnabled: boolean;
}

export const STANDARD_CATEGORIES: Array<{ id: number; label: string }> = [
  { id: 2000, label: 'Movies (2000)' },
  { id: 2040, label: 'Movies HD (2040)' },
  { id: 2045, label: 'Movies UHD (2045)' },
  { id: 5000, label: 'TV (5000)' },
  { id: 5040, label: 'TV HD (5040)' },
  { id: 5045, label: 'TV UHD (5045)' },
  { id: 5070, label: 'Anime (5070)' },
  { id: 3000, label: 'Audio (3000)' },
  { id: 3030, label: 'Audiobooks (3030)' },
  { id: 7000, label: 'Books (7000)' },
  { id: 7020, label: 'E-Books (7020)' },
];

export const NEWZNAB_CATEGORY_LABELS: Record<number, string> = {
  // Movies
  2000: 'Movies (2000)',
  2010: 'Movies Foreign (2010)',
  2020: 'Movies Other (2020)',
  2030: 'Movies SD (2030)',
  2040: 'Movies HD (2040)',
  2045: 'Movies UHD (2045)',
  2050: 'Movies BluRay (2050)',
  2060: 'Movies 3D (2060)',
  2100: 'Movies DE (2100)',
  // Audio
  3000: 'Audio (3000)',
  3010: 'Audio MP3 (3010)',
  3020: 'Audio Lossless (3020)',
  3030: 'Audiobooks (3030)',
  3040: 'Audio Video (3040)',
  // PC
  4000: 'PC (4000)',
  // TV
  5000: 'TV (5000)',
  5020: 'TV Foreign (5020)',
  5030: 'TV SD (5030)',
  5040: 'TV HD (5040)',
  5045: 'TV UHD (5045)',
  5070: 'Anime (5070)',
  5080: 'TV Documentary (5080)',
  5100: 'TV DE (5100)',
  5140: 'TV HD DE (5140)',
  5170: 'Anime DE (5170)',
  // Books
  7000: 'Books (7000)',
  7010: 'Books Mags (7010)',
  7020: 'E-Books (7020)',
  7030: 'Comics (7030)',
  7100: 'Books DE (7100)',
  // Other
  8000: 'Other (8000)',
};

export function getCategoryLabel(id: number): string {
  return NEWZNAB_CATEGORY_LABELS[id] ?? `Category ${id}`;
}

export const DEFAULT_CATEGORIES = [2000, 2040, 2045, 5000, 5040, 5045, 5070];

export interface RemotePathMappingDto {
  id: number;
  downloadClientId: number | null;
  host: string;
  remotePath: string;
  localPath: string;
}

