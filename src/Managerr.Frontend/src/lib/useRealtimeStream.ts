import { useEffect, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';

export interface RealtimeStreamOptions {
  enabled?: boolean;
}

export function useRealtimeStream({ enabled = true }: RealtimeStreamOptions = {}) {
  const qc = useQueryClient();
  const [isConnected, setIsConnected] = useState(false);
  const eventSourceRef = useRef<EventSource | null>(null);
  const reconnectTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    if (!enabled || typeof window === 'undefined' || typeof EventSource === 'undefined') {
      return;
    }

    let isDisposed = false;

    function connect() {
      if (isDisposed) return;

      try {
        const es = new EventSource('/api/v1/realtime/stream', {
          withCredentials: true,
        });
        eventSourceRef.current = es;

        es.onopen = () => {
          if (!isDisposed) setIsConnected(true);
        };

        es.onerror = () => {
          if (!isDisposed) {
            setIsConnected(false);
            es.close();
            // Reconnect with 3s backoff
            reconnectTimeoutRef.current = setTimeout(connect, 3000);
          }
        };

        // Listen for backend RealtimeHub SSE events
        es.addEventListener('TrackedDownloadRefreshed', () => {
          void qc.invalidateQueries({ queryKey: ['download-queue'] });
          void qc.invalidateQueries({ queryKey: ['download-tracked'] });
          void qc.invalidateQueries({ queryKey: ['activity-history'] });
          void qc.invalidateQueries({ queryKey: ['queue-items'] });
        });

        es.addEventListener('MediaImported', () => {
          void qc.invalidateQueries({ queryKey: ['catalog-works'] });
          void qc.invalidateQueries({ queryKey: ['catalog-tree'] });
          void qc.invalidateQueries({ queryKey: ['libraries'] });
          void qc.invalidateQueries({ queryKey: ['wanted'] });
          void qc.invalidateQueries({ queryKey: ['activity-history'] });
        });

        es.addEventListener('TaskUpdated', () => {
          void qc.invalidateQueries({ queryKey: ['system-tasks'] });
          void qc.invalidateQueries({ queryKey: ['system-health'] });
        });

        // Listen for topic-scoped message events
        es.addEventListener('queue:updated', () => {
          void qc.invalidateQueries({ queryKey: ['download-queue'] });
          void qc.invalidateQueries({ queryKey: ['download-tracked'] });
          void qc.invalidateQueries({ queryKey: ['queue-items'] });
        });

        es.addEventListener('history:updated', () => {
          void qc.invalidateQueries({ queryKey: ['activity-history'] });
        });

        es.addEventListener('catalog:updated', () => {
          void qc.invalidateQueries({ queryKey: ['catalog-works'] });
          void qc.invalidateQueries({ queryKey: ['catalog-tree'] });
        });

        es.addEventListener('libraries:updated', () => {
          void qc.invalidateQueries({ queryKey: ['libraries'] });
        });

        es.addEventListener('tasks:updated', () => {
          void qc.invalidateQueries({ queryKey: ['system-tasks'] });
        });

        es.addEventListener('health:updated', () => {
          void qc.invalidateQueries({ queryKey: ['system-health'] });
        });

        es.addEventListener('auth:updated', () => {
          void qc.invalidateQueries({ queryKey: ['auth-status'] });
          void qc.invalidateQueries({ queryKey: ['auth-providers'] });
          void qc.invalidateQueries({ queryKey: ['me'] });
        });

        es.addEventListener('cleanerr.updated', () => {
          void qc.invalidateQueries({
            predicate: (q) =>
              typeof q.queryKey[0] === 'string' && q.queryKey[0].startsWith('cleanerr'),
          });
        });

        es.onmessage = (ev) => {
          try {
            const data = JSON.parse(ev.data);
            if (data?.type === 'ping') return;
            if (data?.topic) {
              void qc.invalidateQueries({ queryKey: [data.topic] });
            }
          } catch {
            // Ignore non-json heartbeats
          }
        };
      } catch {
        if (!isDisposed) {
          reconnectTimeoutRef.current = setTimeout(connect, 5000);
        }
      }
    }

    connect();

    return () => {
      isDisposed = true;
      if (reconnectTimeoutRef.current) clearTimeout(reconnectTimeoutRef.current);
      if (eventSourceRef.current) {
        eventSourceRef.current.close();
        eventSourceRef.current = null;
      }
      setIsConnected(false);
    };
  }, [enabled, qc]);

  return { isConnected };
}
