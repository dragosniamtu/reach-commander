import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ProtectedStateResetService } from '../../core/auth/protected-state-reset.service';
import { SubtitleJobStatus } from './subtitle-generation.models';
import { SUBTITLE_POLL_SCHEDULER, SubtitleGenerationStore } from './subtitle-generation.store';

export function subtitleJob(changes: Partial<SubtitleJobStatus> = {}): SubtitleJobStatus {
  return {
    jobId: 'job-1',
    sourceId: 'media',
    path: '/Film & TV/Movie.mkv',
    english: true,
    romanian: false,
    phase: 'running',
    stage: 'Transcribing',
    currentPath: '/Film & TV/Movie.mkv',
    currentPercent: 30,
    files: [{ path: '/Film & TV/Movie.mkv', phase: 'running', error: null }],
    createdAt: '2026-11-24T12:00:00Z',
    updatedAt: '2026-11-24T12:01:00Z',
    error: null,
    ...changes,
  };
}

describe('SubtitleGenerationStore', () => {
  let store: SubtitleGenerationStore;
  let http: HttpTestingController;
  let scheduled: (() => Promise<void> | void) | null;
  beforeEach(() => {
    scheduled = null;
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: SUBTITLE_POLL_SCHEDULER,
          useValue: {
            schedule: (callback: () => Promise<void> | void) => {
              scheduled = () => {
                scheduled = null;
                return callback();
              };
              return 1;
            },
            cancel: () => {
              scheduled = null;
            },
          },
        },
      ],
    });
    store = TestBed.inject(SubtitleGenerationStore);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  const ready = {
    enabled: true,
    workerAvailable: true,
    englishAvailable: true,
    romanianAvailable: true,
    reasonCode: null,
    detail: null,
  };
  async function configure(capabilities = ready): Promise<void> {
    store.open({
      panelSide: 'left',
      sourceId: 'media',
      sourceName: 'Media',
      path: '/Film & TV/Movie.mkv',
    });
    http.expectOne('/api/subtitle-jobs/capabilities').flush(capabilities);
    await Promise.resolve();
  }
  async function restore(job = subtitleJob()): Promise<void> {
    const pending = store.restore();
    http.expectOne('/api/subtitle-jobs').flush([job]);
    await pending;
  }

  it('defaults to English, requires a language, and resets choices when reopened', async () => {
    await configure();
    expect(store.english()).toBe(true);
    expect(store.romanian()).toBe(false);
    store.english.set(false);
    expect(store.canGenerate()).toBe(false);
    await store.generate();
    http.expectNone('/api/subtitle-jobs');
    store.close();
    await configure();
    expect(store.english()).toBe(true);
  });

  it('captures an immutable batch and submits one durable job even after the dialog closes', async () => {
    const paths = ['/First.mkv', '/Second.mp4'];
    const context = {
      panelSide: 'left' as const,
      sourceId: 'media',
      sourceName: 'Media',
      path: paths[0],
      paths,
    };
    store.open(context);
    http.expectOne('/api/subtitle-jobs/capabilities').flush(ready);
    await Promise.resolve();
    paths.push('/Not selected.mkv');
    store.romanian.set(true);
    store.skipExisting.set(false);
    const pending = store.generate();
    store.close();
    const request = http.expectOne('/api/subtitle-jobs');
    expect(request.request.body).toEqual({
      sourceId: 'media',
      path: '/First.mkv',
      paths: ['/First.mkv', '/Second.mp4'],
      english: true,
      romanian: true,
      skipExisting: false,
    });
    request.flush(
      subtitleJob({
        phase: 'queued',
        files: [
          { path: '/First.mkv', phase: 'pending', error: null },
          { path: '/Second.mp4', phase: 'pending', error: null },
        ],
      }),
    );
    await pending;
    expect(store.jobs()).toHaveLength(1);
    expect(store.jobs()[0].files).toHaveLength(2);
    expect(store.dialog()).toBe('closed');
    expect(scheduled).not.toBeNull();
  });

  it.each([
    [true, false],
    [true, true],
    [false, true],
  ])('submits only logical selection and language flags (%s/%s)', async (english, romanian) => {
    await configure();
    store.english.set(english);
    store.romanian.set(romanian);
    const pending = store.generate();
    const request = http.expectOne('/api/subtitle-jobs');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      sourceId: 'media',
      path: '/Film & TV/Movie.mkv',
      english,
      romanian,
      skipExisting: true,
    });
    request.flush(subtitleJob({ english, romanian }));
    await pending;
    expect(store.dialog()).toBe('jobs');
    expect(store.selectedJob()?.jobId).toBe('job-1');
  });

  it('allows explicit regeneration and restores skipping for the next selection', async () => {
    await configure();
    expect(store.skipExisting()).toBe(true);
    store.skipExisting.set(false);
    const pending = store.generate();
    const request = http.expectOne('/api/subtitle-jobs');
    expect(request.request.body.skipExisting).toBe(false);
    request.flush(subtitleJob({ phase: 'completed' }));
    await pending;
    store.close();
    await configure();
    expect(store.skipExisting()).toBe(true);
  });

  it('recovers active and terminal history after refresh and polls while the dialog is closed', async () => {
    const pending = store.restore();
    http
      .expectOne('/api/subtitle-jobs')
      .flush([subtitleJob(), subtitleJob({ jobId: 'old', phase: 'completed' })]);
    await pending;
    expect(store.dialog()).toBe('closed');
    expect(store.jobs().length).toBe(2);
    expect(store.activeCount()).toBe(1);
    store.openHistory();
    http.expectOne('/api/subtitle-jobs/capabilities').flush(ready);
    http.expectOne('/api/subtitle-jobs').flush([subtitleJob()]);
    await Promise.resolve();
    store.close();
    const polling = scheduled!();
    http.expectOne('/api/subtitle-jobs').flush([subtitleJob({ phase: 'completed' })]);
    await polling;
    expect(store.activeCount()).toBe(0);
    expect(scheduled).toBeNull();
  });

  it('cancels a recovered job and ignores an overlapping stale status response', async () => {
    await restore();
    const polling = scheduled!();
    const stale = http.expectOne('/api/subtitle-jobs');
    const cancelling = store.cancel('job-1');
    const request = http.expectOne('/api/subtitle-jobs/job-1/cancel');
    expect(request.request.method).toBe('POST');
    request.flush(subtitleJob({ phase: 'cancelled' }));
    await cancelling;
    stale.flush([subtitleJob()]);
    await polling;
    expect(store.jobs()[0].phase).toBe('cancelled');
  });

  it('reports submission failure without losing language selection', async () => {
    await configure();
    store.romanian.set(true);
    const pending = store.generate();
    http
      .expectOne('/api/subtitle-jobs')
      .flush(
        { detail: 'Subtitle generation is not configured.' },
        { status: 503, statusText: 'Unavailable' },
      );
    await pending;
    http.expectOne('/api/subtitle-jobs/capabilities').flush({ ...ready, enabled: false });
    await Promise.resolve();
    expect(store.dialog()).toBe('configure');
    expect(store.error()).toContain('not configured');
    expect(store.romanian()).toBe(true);
    expect(store.submitting()).toBe(false);
    expect(store.canGenerate()).toBe(false);
  });

  it('submits a folder without enumerating or uploading its files in the browser', async () => {
    store.open({
      panelSide: 'right',
      sourceId: 'media',
      sourceName: 'Media',
      path: '/TV/Season 1',
    });
    http.expectOne('/api/subtitle-jobs/capabilities').flush(ready);
    await Promise.resolve();
    const pending = store.generate();
    const request = http.expectOne('/api/subtitle-jobs');
    expect(request.request.body).toEqual({
      sourceId: 'media',
      path: '/TV/Season 1',
      english: true,
      romanian: false,
      skipExisting: true,
    });
    request.flush(
      subtitleJob({
        path: '/TV/Season 1',
        phase: 'discovering',
        currentPath: null,
        currentPercent: null,
        files: [],
      }),
    );
    await pending;
    expect(store.selectedJob()?.phase).toBe('discovering');
  });

  it('refreshes visible files once after a terminal job and retains per-file failure details', async () => {
    const completed = vi.fn();
    store.setCompletionHandler(completed);
    await restore();
    await restore(
      subtitleJob({
        phase: 'completed',
        files: [
          { path: '/Bad.mkv', phase: 'failed', error: 'No audio stream.' },
          { path: '/Good.mkv', phase: 'completed', error: null },
        ],
      }),
    );
    expect(store.selectedJob()?.files[0].error).toBe('No audio stream.');
    await restore(subtitleJob({ phase: 'completed' }));
    expect(completed).toHaveBeenCalledOnce();
  });

  it('does not reopen a dismissed dialog when a submitted job is accepted later', async () => {
    await configure();
    const pending = store.generate();
    const request = http.expectOne('/api/subtitle-jobs');
    store.close();
    request.flush(subtitleJob());
    await pending;
    expect(store.dialog()).toBe('closed');
    expect(store.activeCount()).toBe(1);
    expect(scheduled).not.toBeNull();
  });

  it('retains history and retries when polling or cancellation fails', async () => {
    await restore();
    const polling = scheduled!();
    http.expectOne('/api/subtitle-jobs').flush({}, { status: 500, statusText: 'Failed' });
    await polling;
    expect(store.jobs().length).toBe(1);
    expect(store.error()).toBeTruthy();
    expect(scheduled).not.toBeNull();
    const cancelling = store.cancel('job-1');
    http
      .expectOne('/api/subtitle-jobs/job-1/cancel')
      .flush({}, { status: 500, statusText: 'Failed' });
    await cancelling;
    expect(store.jobs()[0].phase).toBe('running');
    expect(store.canCancel(store.jobs()[0])).toBe(true);
  });

  it('clears private history and rejects in-flight responses after authentication reset', async () => {
    const pending = store.restore();
    const request = http.expectOne('/api/subtitle-jobs');
    await configure();
    TestBed.inject(ProtectedStateResetService).reset();
    request.flush([subtitleJob()]);
    await pending;
    expect(store.jobs()).toEqual([]);
    expect(store.context()).toBeNull();
    expect(store.dialog()).toBe('closed');
    expect(scheduled).toBeNull();
  });

  it('blocks submission until server readiness has loaded', async () => {
    store.open({ panelSide: 'left', sourceId: 'media', sourceName: 'Media', path: '/Movie.mkv' });
    expect(store.canGenerate()).toBe(false);
    await store.generate();
    http.expectNone('/api/subtitle-jobs');
    http.expectOne('/api/subtitle-jobs/capabilities').flush(ready);
    await Promise.resolve();
    expect(store.canGenerate()).toBe(true);
  });

  it('requires every selected language to be available', async () => {
    await configure({ ...ready, romanianAvailable: false });
    expect(store.canGenerate()).toBe(true);
    store.romanian.set(true);
    expect(store.canGenerate()).toBe(false);
    await store.generate();
    http.expectNone('/api/subtitle-jobs');
    store.romanian.set(false);
    expect(store.canGenerate()).toBe(true);
  });

  it.each([
    { ...ready, enabled: false },
    { ...ready, workerAvailable: false },
    { ...ready, englishAvailable: false },
  ])(
    'blocks unavailable generation but still allows history and cancellation (%j)',
    async (capabilities) => {
      await configure(capabilities);
      expect(store.canGenerate()).toBe(false);
      await restore();
      expect(store.jobs()).toHaveLength(1);
      expect(store.canCancel(store.jobs()[0])).toBe(true);
      const pending = store.cancel('job-1');
      http.expectOne('/api/subtitle-jobs/job-1/cancel').flush(subtitleJob({ phase: 'cancelled' }));
      await pending;
      expect(store.jobs()[0].phase).toBe('cancelled');
    },
  );

  it('stops background history retries when the feature is disabled', async () => {
    await restore();
    expect(scheduled).not.toBeNull();
    await configure({ ...ready, enabled: false });
    expect(scheduled).toBeNull();
    const pending = store.restore();
    http.expectOne('/api/subtitle-jobs').flush({}, { status: 503, statusText: 'Unavailable' });
    await pending;
    expect(scheduled).toBeNull();
    expect(store.jobs()).toHaveLength(1);
  });

  it('fails closed on a readiness error and can recover when reopened', async () => {
    store.open({ panelSide: 'left', sourceId: 'media', sourceName: 'Media', path: '/Movie.mkv' });
    http
      .expectOne('/api/subtitle-jobs/capabilities')
      .flush({}, { status: 503, statusText: 'Unavailable' });
    await Promise.resolve();
    expect(store.canGenerate()).toBe(false);
    expect(scheduled).toBeNull();
    store.close();
    await configure();
    expect(store.canGenerate()).toBe(true);
  });

  it('retains a disabled state when rechecking fails so history cannot restart background retries', async () => {
    await configure({ ...ready, enabled: false });
    const checking = store.loadCapabilities();
    http
      .expectOne('/api/subtitle-jobs/capabilities')
      .flush({}, { status: 503, statusText: 'Unavailable' });
    await checking;
    const pending = store.restore();
    http.expectOne('/api/subtitle-jobs').flush({}, { status: 500, statusText: 'Failed' });
    await pending;
    expect(store.canGenerate()).toBe(false);
    expect(scheduled).toBeNull();
  });
});
