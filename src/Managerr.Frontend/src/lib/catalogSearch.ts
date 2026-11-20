export interface CatalogSearchWorkSummary {
  id: number;
  libraryId?: number;
  workKind?: string;
  title: string;
  altTitles?: string[];
  year?: number | null;
  posterUrl?: string | null;
  externalIds?: Record<string, string>;
  mediaType?: string;
}

export function collapseCatalogQuery(value: string): string {
  return value
    .normalize('NFKD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '');
}

/**
 * Filter altTitles to readable Latin script, excluding CJK, Hangul, Cyrillic, etc.
 */
export function isReadableLatinTitle(s: string): boolean {
  if (!s || !/[A-Za-z0-9]/.test(s)) return false;
  return !/[\u3040-\u30ff\u3400-\u9fff\uf900-\ufaff\uac00-\ud7af\u0400-\u04ff]/.test(s);
}

/**
 * Shared space/punctuation-tolerant matcher for catalog search & library filtering.
 * Supports exact substrings on title, altTitles, year, externalIds (key:value),
 * as well as collapsed search (e.g. "onepiece" -> "One Piece", "spiderman" -> "Spider-Man").
 */
export function matchesCatalogWorkQuery(
  work: {
    title: string;
    altTitles?: string[];
    year?: number | null;
    externalIds?: Record<string, string>;
  },
  rawQuery: string,
): boolean {
  const q = rawQuery.trim().toLowerCase();
  if (!q) return true;

  // Normal lowercase matching
  const inTitle = work.title.toLowerCase().includes(q);
  const inAlt = (work.altTitles ?? []).some((a) => a.toLowerCase().includes(q));
  const inYear = work.year != null && String(work.year).includes(q);
  const inExt =
    work.externalIds != null &&
    Object.entries(work.externalIds).some(([k, v]) => `${k}:${v}`.toLowerCase().includes(q));

  if (inTitle || inAlt || inYear || inExt) {
    return true;
  }

  // Collapsed space / punctuation-tolerant matching (length >= 2)
  const collapsedQ = collapseCatalogQuery(q);
  if (collapsedQ.length >= 2) {
    if (collapseCatalogQuery(work.title).includes(collapsedQ)) {
      return true;
    }
    if ((work.altTitles ?? []).some((a) => collapseCatalogQuery(a).includes(collapsedQ))) {
      return true;
    }
  }

  return false;
}
