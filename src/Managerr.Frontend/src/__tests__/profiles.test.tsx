import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ProfilesAndQuarantineView } from '../components/ProfilesAndQuarantineView';

describe('ProfilesAndQuarantineView & Custom Format Editor', () => {
  it('creates a Quality Profile and a Custom Format with regex specification rules', async () => {
    const user = userEvent.setup();
    const mutations: Array<{ method: string; path: string; body: Record<string, unknown> }> = [];

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const rawUrl = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      const url = new URL(rawUrl, 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      const json = (data: unknown, status = 200) =>
        new Response(JSON.stringify(data), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (method !== 'GET' && init?.body) {
        mutations.push({
          method,
          path: url.pathname,
          body: JSON.parse(String(init.body)) as Record<string, unknown>,
        });
      }

      if (url.pathname === '/api/v1/profiles' && method === 'GET') {
        return json([
          {
            id: 1,
            name: 'German First — Best Quality',
            slug: 'german-first-best-quality',
            mediaVertical: 'video',
            qualityCutoff: 'Remux-2160p',
            weightQuality: 1.0,
            weightLanguage: 2.0,
            weightTraits: 1.0,
            isSystemPreset: true,
            languagePolicyId: 10,
            languagePolicy: {
              id: 10,
              name: 'German First',
              slug: 'german-first',
              minimumAbsoluteQuality: 'WEBDL-1080p',
              enableLanguageUpgrades: true,
            },
          },
        ]);
      }

      if (url.pathname === '/api/v1/profiles/language-policies') {
        return json([
          {
            id: 10,
            name: 'German First',
            slug: 'german-first',
            minimumAbsoluteQuality: 'WEBDL-1080p',
            enableLanguageUpgrades: true,
          },
        ]);
      }

      if (url.pathname === '/api/v1/profiles' && method === 'POST') {
        return json(
          {
            id: 2,
            name: 'Anime Dual Audio 1080p',
            slug: 'anime-dual-audio-1080p',
            mediaVertical: 'video',
            qualityCutoff: 'BluRay-1080p',
            weightQuality: 1.0,
            weightLanguage: 2.0,
            weightTraits: 1.0,
            isSystemPreset: false,
          },
          201,
        );
      }

      if (url.pathname === '/api/v1/profiles/custom-formats' && method === 'GET') return json([]);
      if (url.pathname === '/api/v1/profiles/custom-formats' && method === 'POST') {
        return json(
          {
            id: 15,
            name: 'German TrueHD Atmos',
            trashId: null,
            defaultScore: 250,
            includeWhenRenaming: true,
            containsLanguageRule: false,
            specifications: [],
          },
          201,
        );
      }
      if (url.pathname === '/api/v1/profiles/release-profiles') return json([]);
      if (url.pathname === '/api/v1/profiles/delay-profiles') return json([]);
      if (url.pathname === '/api/v1/profiles/quality-sizes') return json([]);
      if (url.pathname === '/api/v1/indexers') return json([]);

      return json([]);
    });

    vi.stubGlobal('fetch', fetchMock);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <ProfilesAndQuarantineView
          section="profiles"
          canReadProfiles={true}
          canWriteProfiles={true}
          canReadVerification={true}
          canReviewQuarantine={true}
        />
      </QueryClientProvider>,
    );

    // 1. Create a new Quality Profile
    await user.click(await screen.findByRole('button', { name: /^Add Profile$/i }));
    expect(screen.getByRole('heading', { level: 2, name: 'Add Quality Profile' })).toBeInTheDocument();

    await user.type(screen.getByPlaceholderText('e.g. German First — Best Quality'), 'Anime Dual Audio 1080p');
    await user.selectOptions(screen.getByDisplayValue('Remux-2160p'), 'BluRay-1080p');
    await user.click(screen.getByRole('button', { name: /^Save$/i }));

    await waitFor(() => {
      const profilePost = mutations.find((m) => m.method === 'POST' && m.path === '/api/v1/profiles');
      expect(profilePost).toBeDefined();
      expect(profilePost!.body).toMatchObject({
        name: 'Anime Dual Audio 1080p',
        mediaVertical: 'video',
        qualityCutoff: 'BluRay-1080p',
        languagePolicyId: 10,
      });
    });

    // 2. Create a Custom Format with specifications
    await user.click(screen.getByRole('button', { name: /Add Custom Format/i }));
    expect(screen.getByRole('heading', { level: 2, name: 'Add Custom Format' })).toBeInTheDocument();

    await user.type(screen.getByPlaceholderText('e.g. HDR10+'), 'German TrueHD Atmos');
    await user.click(screen.getByLabelText(/Include when renaming files/i));
    await user.click(screen.getByLabelText(/^Required$/i));

    const patternInput = screen.getByPlaceholderText(/HDR10\(Plus/i);
    await user.type(patternInput, 'TrueHD.Atmos');

    await user.click(screen.getByRole('button', { name: 'Save Custom Format' }));

    await waitFor(() => {
      const cfPost = mutations.find((m) => m.method === 'POST' && m.path === '/api/v1/profiles/custom-formats');
      expect(cfPost).toBeDefined();
      expect(cfPost!.body).toMatchObject({
        name: 'German TrueHD Atmos',
        defaultScore: 100,
        includeWhenRenaming: true,
        specifications: [
          {
            name: 'Release Title Match',
            implementation: 'ReleaseTitleSpecification',
            negate: false,
            required: true,
            pattern: 'TrueHD.Atmos',
          },
        ],
      });
    });
  });
});
