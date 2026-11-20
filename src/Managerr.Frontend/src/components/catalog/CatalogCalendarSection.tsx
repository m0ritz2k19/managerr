import {
  Calendar as CalendarIcon,
  CalendarRange,
  Check,
  Copy,
  ExternalLink,
  Sparkles,
} from 'lucide-react';
import clsx from 'clsx';
import {
  ArrButton,
  ArrLabel,
  ArrModal,
  FieldSet,
} from '../arr/ArrPrimitives';
import type { CalendarEntry } from './catalogTypesAndHelpers';

export function computeCalendarDateModel(calendarViewMode: 'month' | 'week' | 'agenda', calendarAnchorDate: Date) {
  function toIso(d: Date): string {
    const y = d.getFullYear();
    const m = String(d.getMonth() + 1).padStart(2, '0');
    const day = String(d.getDate()).padStart(2, '0');
    return `${y}-${m}-${day}`;
  }

  const now = new Date();
  const todayStr = toIso(now);
  const anchor = calendarAnchorDate;
  const year = anchor.getFullYear();
  const month = anchor.getMonth();

  if (calendarViewMode === 'month') {
    const firstDay = new Date(year, month, 1);
    const startDow = (firstDay.getDay() + 6) % 7;
    const gridStart = new Date(year, month, 1 - startDow);

    const lastDay = new Date(year, month + 1, 0);
    const endDow = (lastDay.getDay() + 6) % 7;
    const gridEnd = new Date(year, month + 1, 6 - endDow);

    const startIso = toIso(gridStart);
    const endIso = toIso(gridEnd);

    const rangeLabel = anchor.toLocaleDateString(undefined, {
      month: 'long',
      year: 'numeric',
    });

    const days: Array<{
      date: Date;
      dateStr: string;
      dayNum: number;
      isCurrentMonth: boolean;
      isToday: boolean;
    }> = [];

    const cursor = new Date(gridStart);
    while (cursor <= gridEnd) {
      const curIso = toIso(cursor);
      days.push({
        date: new Date(cursor),
        dateStr: curIso,
        dayNum: cursor.getDate(),
        isCurrentMonth: cursor.getMonth() === month,
        isToday: curIso === todayStr,
      });
      cursor.setDate(cursor.getDate() + 1);
    }

    return {
      calStartDateIso: startIso,
      calEndDateIso: endIso,
      calRangeLabel: rangeLabel,
      calMonthGridDays: days,
      calWeekDays: [] as Array<{
        date: Date;
        dateStr: string;
        dayNum: number;
        dayName: string;
        isToday: boolean;
      }>,
    };
  }

  if (calendarViewMode === 'week') {
    const dow = (anchor.getDay() + 6) % 7;
    const weekStart = new Date(year, month, anchor.getDate() - dow);
    const weekEnd = new Date(weekStart.getFullYear(), weekStart.getMonth(), weekStart.getDate() + 6);

    const startIso = toIso(weekStart);
    const endIso = toIso(weekEnd);

    const rangeLabel = `${weekStart.toLocaleDateString(undefined, {
      month: 'short',
      day: 'numeric',
    })} – ${weekEnd.toLocaleDateString(undefined, {
      month: 'short',
      day: 'numeric',
      year: 'numeric',
    })}`;

    const days: Array<{
      date: Date;
      dateStr: string;
      dayNum: number;
      dayName: string;
      isToday: boolean;
    }> = [];

    const cursor = new Date(weekStart);
    for (let i = 0; i < 7; i++) {
      const curIso = toIso(cursor);
      days.push({
        date: new Date(cursor),
        dateStr: curIso,
        dayNum: cursor.getDate(),
        dayName: cursor.toLocaleDateString(undefined, { weekday: 'short' }),
        isToday: curIso === todayStr,
      });
      cursor.setDate(cursor.getDate() + 1);
    }

    return {
      calStartDateIso: startIso,
      calEndDateIso: endIso,
      calRangeLabel: rangeLabel,
      calMonthGridDays: [] as Array<{
        date: Date;
        dateStr: string;
        dayNum: number;
        isCurrentMonth: boolean;
        isToday: boolean;
      }>,
      calWeekDays: days,
    };
  }

  const agendaStart = new Date(year, month, anchor.getDate() - 7);
  const agendaEnd = new Date(year, month, anchor.getDate() + 30);
  const startIso = toIso(agendaStart);
  const endIso = toIso(agendaEnd);
  const rangeLabel = `${agendaStart.toLocaleDateString(undefined, {
    month: 'short',
    day: 'numeric',
  })} – ${agendaEnd.toLocaleDateString(undefined, {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
  })}`;

  return {
    calStartDateIso: startIso,
    calEndDateIso: endIso,
    calRangeLabel: rangeLabel,
    calMonthGridDays: [] as Array<{
      date: Date;
      dateStr: string;
      dayNum: number;
      isCurrentMonth: boolean;
      isToday: boolean;
    }>,
    calWeekDays: [] as Array<{
      date: Date;
      dateStr: string;
      dayNum: number;
      dayName: string;
      isToday: boolean;
    }>,
  };
}

interface CatalogCalendarSectionProps {
  calRangeLabel: string;
  filteredCalendarItems: CalendarEntry[];
  calendarViewMode: 'month' | 'week' | 'agenda';
  calMonthGridDays: Array<{
    date: Date;
    dateStr: string;
    dayNum: number;
    isCurrentMonth: boolean;
    isToday: boolean;
  }>;
  calWeekDays: Array<{
    date: Date;
    dateStr: string;
    dayNum: number;
    dayName: string;
    isToday: boolean;
  }>;
  calendarItemsByDate: Record<string, CalendarEntry[]>;
  onOpenWork: (workId: number) => void;
  showIcalModal: boolean;
  onSetShowIcalModal: (v: boolean) => void;
  icalVertical: string;
  onSetIcalVertical: (v: string) => void;
  icalIncludeUnmonitored: boolean;
  onSetIcalIncludeUnmonitored: (v: boolean) => void;
  icalPastDays: number;
  onSetIcalPastDays: (v: number) => void;
  icalFutureDays: number;
  onSetIcalFutureDays: (v: number) => void;
  icalApiKey: string;
  onSetIcalApiKey: (v: string) => void;
  onGenerateCalendarToken: () => void;
  isGeneratingCalendarToken: boolean;
  icalFeedUrl: string;
  webcalFeedUrl: string;
  copiedIcalUrl: boolean;
  onSetCopiedIcalUrl: (v: boolean) => void;
}

export function CatalogCalendarSection({
  calRangeLabel,
  filteredCalendarItems,
  calendarViewMode,
  calMonthGridDays,
  calWeekDays,
  calendarItemsByDate,
  onOpenWork,
  showIcalModal,
  onSetShowIcalModal,
  icalVertical,
  onSetIcalVertical,
  icalIncludeUnmonitored,
  onSetIcalIncludeUnmonitored,
  icalPastDays,
  onSetIcalPastDays,
  icalFutureDays,
  onSetIcalFutureDays,
  icalApiKey,
  onSetIcalApiKey,
  onGenerateCalendarToken,
  isGeneratingCalendarToken,
  icalFeedUrl,
  webcalFeedUrl,
  copiedIcalUrl,
  onSetCopiedIcalUrl,
}: CatalogCalendarSectionProps) {
  return (
    <div className="space-y-4">
      <FieldSet
        legend={
          <div className="flex items-center gap-2">
            <CalendarIcon className="h-5 w-5 text-arr-accent" />
            <span>
              Calendar — {calRangeLabel}{' '}
              <span className="text-[14px] text-[#909293] font-normal">
                ({filteredCalendarItems.length}{' '}
                {filteredCalendarItems.length === 1 ? 'release' : 'releases'})
              </span>
            </span>
          </div>
        }
        action={
          <ArrButton
            variant="default"
            className="h-[30px] px-2.5 text-[12px]"
            onClick={() => onSetShowIcalModal(true)}
          >
            <CalendarRange className="h-3.5 w-3.5" /> iCal Link (.ics)
          </ArrButton>
        }
      >
        {filteredCalendarItems.length === 0 && calendarViewMode === 'agenda' ? (
          <div className="bg-arr-card border border-arr-card-border rounded-[4px] p-8 text-center text-[13px] text-[#909293]">
            No scheduled releases in the active date range ({calRangeLabel}). Use <strong>&lt; Prev</strong> or{' '}
            <strong>Next &gt;</strong> to navigate dates or subscribe to the <strong>iCal Link</strong>.
          </div>
        ) : calendarViewMode === 'month' ? (
          /* Month 7-Column Grid */
          <div className="border border-arr-card-border rounded-[4px] overflow-hidden bg-arr-page">
            <div className="grid grid-cols-7 border-b border-arr-card-border bg-[#282828] text-center text-[12px] font-semibold text-[#b8b8b8] py-2">
              <div>Mon</div>
              <div>Tue</div>
              <div>Wed</div>
              <div>Thu</div>
              <div>Fri</div>
              <div>Sat</div>
              <div>Sun</div>
            </div>

            <div className="grid grid-cols-7 bg-arr-page">
              {calMonthGridDays.map((day) => {
                const itemsOnDay = calendarItemsByDate[day.dateStr] ?? [];

                return (
                  <div
                    key={day.dateStr}
                    className={clsx(
                      'min-h-[115px] p-1.5 border-b border-r border-arr-card-border flex flex-col transition-colors',
                      !day.isCurrentMonth && 'bg-[#1a1a1a] opacity-50',
                      day.isToday && 'bg-[#252c34] ring-1 ring-inset ring-arr-accent/50',
                    )}
                  >
                    <div className="flex items-center justify-between mb-1">
                      <span
                        className={clsx(
                          'text-[12px] font-medium leading-none',
                          day.isToday
                            ? 'h-5 w-5 rounded-full bg-arr-accent text-arr-accent-fg font-bold flex items-center justify-center'
                            : day.isCurrentMonth
                              ? 'text-[#e1e2e3]'
                              : 'text-[#666666]',
                        )}
                      >
                        {day.dayNum}
                      </span>
                      {itemsOnDay.length > 0 && (
                        <span className="text-[10px] font-mono text-[#888888]">
                          {itemsOnDay.length}
                        </span>
                      )}
                    </div>

                    <div className="space-y-1 overflow-y-auto max-h-[85px] flex-1">
                      {itemsOnDay.map((item) => {
                        const titleDisplay = `${item.workTitle} ${
                          item.unitNumber ? `S${item.containerTitle || ''}E${item.unitNumber}` : ''
                        }`;

                        return (
                          <button
                            key={item.unitId}
                            type="button"
                            title={`${item.workTitle} — ${item.containerTitle} #${item.unitNumber}: ${item.unitTitle} (${item.hasFile ? 'Downloaded' : item.monitored ? 'Missing' : 'Unmonitored'})`}
                            onClick={() => onOpenWork(item.workId)}
                            className={clsx(
                              'w-full text-left px-1.5 py-0.5 rounded text-[11px] font-medium truncate flex items-center gap-1.5 border transition-all cursor-pointer',
                              item.hasFile
                                ? 'bg-[#18361e] border-[#27c24c]/40 text-[#a3e9b2] hover:bg-[#204928]'
                                : item.monitored
                                  ? 'bg-[#3b1d22] border-[#f05050]/40 text-[#fca5a5] hover:bg-[#4f262d]'
                                  : 'bg-[#292c31] border-[#424750] text-[#9ca3af] hover:bg-[#34383f]',
                            )}
                          >
                            <span
                              className={clsx(
                                'h-1.5 w-1.5 rounded-full shrink-0',
                                item.hasFile
                                  ? 'bg-[#27c24c]'
                                  : item.monitored
                                    ? 'bg-[#f05050]'
                                    : 'bg-[#777777]',
                              )}
                            />
                            <span className="truncate">{titleDisplay}</span>
                          </button>
                        );
                      })}
                    </div>
                  </div>
                );
              })}
            </div>
          </div>
        ) : calendarViewMode === 'week' ? (
          /* Week 7-Day Columns */
          <div className="grid grid-cols-1 md:grid-cols-7 gap-2">
            {calWeekDays.map((day) => {
              const itemsOnDay = calendarItemsByDate[day.dateStr] ?? [];

              return (
                <div
                  key={day.dateStr}
                  className={clsx(
                    'bg-arr-heading border rounded-[4px] p-2 flex flex-col min-h-[300px]',
                    day.isToday ? 'border-arr-accent bg-[#23282f]' : 'border-arr-card-border',
                  )}
                >
                  <div className="flex items-center justify-between pb-2 mb-2 border-b border-arr-card-border">
                    <div>
                      <div className="text-[11px] font-semibold text-[#888888] uppercase">
                        {day.dayName}
                      </div>
                      <div
                        className={clsx(
                          'text-[15px] font-bold leading-tight',
                          day.isToday ? 'text-arr-accent' : 'text-[#eeeeee]',
                        )}
                      >
                        {day.dayNum}
                      </div>
                    </div>
                    <ArrLabel kind={itemsOnDay.length > 0 ? 'primary' : 'default'} className="text-[10px]">
                      {itemsOnDay.length}
                    </ArrLabel>
                  </div>

                  <div className="space-y-2 overflow-y-auto flex-1">
                    {itemsOnDay.length === 0 ? (
                      <div className="text-[11px] text-[#666666] italic text-center py-6">
                        No releases
                      </div>
                    ) : (
                      itemsOnDay.map((item) => (
                        <div
                          key={item.unitId}
                          className={clsx(
                            'p-2 rounded border space-y-1',
                            item.hasFile
                              ? 'bg-[#18361e]/60 border-[#27c24c]/30'
                              : item.monitored
                                ? 'bg-[#3b1d22]/60 border-[#f05050]/30'
                                : 'bg-[#292c31]/60 border-[#424750]/30',
                          )}
                        >
                          <button
                            type="button"
                            onClick={() => onOpenWork(item.workId)}
                            className="text-[12px] font-semibold text-[#e1e2e3] hover:text-arr-accent text-left line-clamp-2 cursor-pointer transition-colors"
                          >
                            {item.workTitle}
                          </button>
                          <div className="text-[11px] text-[#909293] truncate">
                            {item.containerTitle} #{item.unitNumber}
                            {item.unitTitle ? ` — ${item.unitTitle}` : ''}
                          </div>
                          <div className="flex items-center justify-between pt-1">
                            <ArrLabel kind="default" className="text-[10px]">
                              {item.workKind}
                            </ArrLabel>
                            <ArrLabel
                              kind={item.hasFile ? 'success' : item.monitored ? 'danger' : 'default'}
                              className="text-[10px]"
                            >
                              {item.hasFile ? 'Downloaded' : item.monitored ? 'Missing' : 'Unmonitored'}
                            </ArrLabel>
                          </div>
                        </div>
                      ))
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        ) : (
          /* Agenda Chronological View */
          <div className="overflow-x-auto border border-arr-card-border rounded-[4px]">
            <table className="arr-table w-full">
              <thead>
                <tr>
                  <th>Date</th>
                  <th>Work</th>
                  <th>Container</th>
                  <th>Unit</th>
                  <th>Vertical</th>
                  <th className="text-right">Status</th>
                </tr>
              </thead>
              <tbody>
                {filteredCalendarItems.map((item) => (
                  <tr key={item.unitId} className="hover:bg-[#2b2b2b]">
                    <td className="font-mono text-[12px] text-arr-accent">
                      {item.airOrReleaseDate?.slice(0, 10)}
                    </td>
                    <td>
                      <button
                        type="button"
                        onClick={() => onOpenWork(item.workId)}
                        className="font-semibold text-[#eeeeee] hover:text-arr-accent cursor-pointer text-left transition-colors"
                      >
                        {item.workTitle}
                      </button>
                    </td>
                    <td className="text-[13px] text-[#cccccc]">{item.containerTitle}</td>
                    <td className="text-[12px] text-[#b8b8b8]">
                      #{item.unitNumber} — {item.unitTitle}
                    </td>
                    <td>
                      <ArrLabel kind="default">{item.workKind}</ArrLabel>
                    </td>
                    <td className="text-right">
                      <ArrLabel
                        kind={item.hasFile ? 'success' : item.monitored ? 'danger' : 'default'}
                      >
                        {item.hasFile ? 'Downloaded' : item.monitored ? 'Missing' : 'Unmonitored'}
                      </ArrLabel>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </FieldSet>

      {/* ==================== ICAL / .ICS SUBSCRIPTION MODAL ==================== */}
      {showIcalModal && (
        <ArrModal
          title={
            <div className="flex items-center gap-2">
              <CalendarRange className="h-5 w-5 text-arr-accent" />
              <span>Subscribe to Calendar (iCal .ics Feed)</span>
            </div>
          }
          onClose={() => onSetShowIcalModal(false)}
          maxWidth="max-w-2xl"
          footer={
            <ArrButton onClick={() => onSetShowIcalModal(false)}>Close</ArrButton>
          }
        >
          <div className="space-y-4">
            <p className="text-[13px] text-[#b8b8b8]">
              Subscribe to this iCal calendar feed in Apple Calendar, Google Calendar, Outlook, or Home Assistant to view your scheduled release schedule anywhere.
            </p>

            <div className="p-3.5 bg-arr-card border border-arr-card-border rounded-[4px] space-y-3">
              <div className="text-[12px] font-semibold text-arr-accent uppercase tracking-wide">
                Feed Customization
              </div>

              <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 text-[13px]">
                <div>
                  <label className="text-[12px] text-[#909293] block mb-1">Filter by Vertical</label>
                  <select
                    value={icalVertical}
                    onChange={(e) => onSetIcalVertical(e.target.value)}
                    className="arr-input !h-[32px] text-[12px]"
                  >
                    <option value="all">All Media</option>
                    <option value="movies">Movies</option>
                    <option value="tv">TV Series</option>
                    <option value="anime">Anime</option>
                    <option value="music">Music</option>
                    <option value="ebooks">Books &amp; Audio</option>
                  </select>
                </div>

                <div>
                  <label className="text-[12px] text-[#909293] block mb-1">Unmonitored Releases</label>
                  <label className="flex items-center gap-2 cursor-pointer mt-2">
                    <input
                      type="checkbox"
                      checked={icalIncludeUnmonitored}
                      onChange={(e) => onSetIcalIncludeUnmonitored(e.target.checked)}
                      className="accent-arr-accent h-4 w-4"
                    />
                    <span className="text-[13px] text-[#eeeeee]">Include unmonitored items</span>
                  </label>
                </div>

                <div>
                  <label className="text-[12px] text-[#909293] block mb-1">Past Window (Days)</label>
                  <input
                    type="number"
                    min="0"
                    max="90"
                    value={icalPastDays}
                    onChange={(e) => onSetIcalPastDays(Number(e.target.value) || 7)}
                    className="arr-input !h-[32px] font-mono text-[12px]"
                  />
                </div>

                <div>
                  <label className="text-[12px] text-[#909293] block mb-1">Future Window (Days)</label>
                  <input
                    type="number"
                    min="1"
                    max="365"
                    value={icalFutureDays}
                    onChange={(e) => onSetIcalFutureDays(Number(e.target.value) || 30)}
                    className="arr-input !h-[32px] font-mono text-[12px]"
                  />
                </div>
              </div>
            </div>

            <div className="p-3.5 bg-arr-card border border-arr-card-border rounded-[4px] space-y-3">
              <div className="flex items-center justify-between">
                <div className="text-[12px] font-semibold text-arr-accent uppercase tracking-wide">
                  API Key Authentication
                </div>
                <ArrButton
                  variant="default"
                  className="h-[26px] px-2 text-[11px]"
                  busy={isGeneratingCalendarToken}
                  onClick={onGenerateCalendarToken}
                >
                  <Sparkles className="h-3 w-3" /> Generate API Key
                </ArrButton>
              </div>

              <div>
                <input
                  value={icalApiKey}
                  onChange={(e) => onSetIcalApiKey(e.target.value.trim())}
                  placeholder="Click 'Generate API Key' or paste an existing token"
                  className="arr-input font-mono text-[12px]"
                />
                <div className="text-[11px] text-[#888888] mt-1">
                  Calendar iCal feeds authenticate through API key query parameters (e.g. <code>?apikey=...</code>).
                </div>
              </div>
            </div>

            {/* Ready-to-use Link */}
            <div className="space-y-2">
              <label className="text-[12px] font-bold text-[#e1e2e3]">Subscription Feed URL</label>
              <div className="flex items-center gap-2">
                <input
                  readOnly
                  value={icalFeedUrl}
                  className="arr-input font-mono text-[12px] bg-[#1a1a1a] select-all flex-1"
                />
                <ArrButton
                  variant={copiedIcalUrl ? 'success' : 'primary'}
                  className="h-[34px] px-3 shrink-0"
                  onClick={() => {
                    void navigator.clipboard.writeText(icalFeedUrl);
                    onSetCopiedIcalUrl(true);
                    setTimeout(() => onSetCopiedIcalUrl(false), 2500);
                  }}
                >
                  {copiedIcalUrl ? (
                    <>
                      <Check className="h-3.5 w-3.5" /> Copied!
                    </>
                  ) : (
                    <>
                      <Copy className="h-3.5 w-3.5" /> Copy
                    </>
                  )}
                </ArrButton>
              </div>
            </div>

            <div className="flex flex-wrap items-center gap-2 pt-1">
              <a
                href={webcalFeedUrl}
                className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-[4px] bg-arr-card hover:bg-[#444444] border border-[#4a5158] text-[12px] font-medium text-arr-accent transition-colors"
              >
                <ExternalLink className="h-3.5 w-3.5" /> Subscribe directly with WebCal
              </a>
            </div>

            <div className="p-3 bg-[#252525] border border-arr-card-border rounded-[4px] text-[12px] text-[#909293] space-y-1">
              <div className="font-semibold text-[#cccccc]">How to Subscribe:</div>
              <div>&bull; <strong>Apple Calendar:</strong> File &rarr; New Calendar Subscription &rarr; Paste URL.</div>
              <div>&bull; <strong>Google Calendar:</strong> Other calendars (+) &rarr; From URL &rarr; Paste URL.</div>
              <div>&bull; <strong>Outlook Web:</strong> Add Calendar &rarr; Subscribe from web &rarr; Paste URL.</div>
            </div>
          </div>
        </ArrModal>
      )}
    </div>
  );
}
