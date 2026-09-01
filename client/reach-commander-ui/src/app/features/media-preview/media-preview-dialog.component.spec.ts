import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MediaPreviewState } from '../../core/state/media-preview.models';
import { MediaPreviewStore } from '../../core/state/media-preview.store';
import { MediaPreviewDialogComponent } from './media-preview-dialog.component';

describe('MediaPreviewDialogComponent', () => {
  let fixture: ComponentFixture<MediaPreviewDialogComponent>;
  const store = {
    state: signal<MediaPreviewState>(readyState()),
    activeCue: signal({
      index: 0, startMilliseconds: 1_000, endMilliseconds: 2_000, text: 'Hello',
    }),
    adjustedCues: signal([]),
    canPlanSave: signal(true),
    canExecuteSave: signal(false),
    mediaUrl: signal<string | null>(null),
    setOffset: vi.fn(),
    setVideoTime: vi.fn(),
    selectSubtitle: vi.fn(() => Promise.resolve()),
    planSave: vi.fn(() => Promise.resolve()),
    executeSave: vi.fn(() => Promise.resolve()),
    retryWithFallback: vi.fn(() => Promise.resolve()),
    close: vi.fn(() => Promise.resolve()),
  };

  beforeEach(async () => {
    vi.clearAllMocks();
    vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => undefined);
    vi.spyOn(HTMLMediaElement.prototype, 'load').mockImplementation(() => undefined);
    store.state.set(readyState());
    store.activeCue.set({
      index: 0, startMilliseconds: 1_000, endMilliseconds: 2_000, text: 'Hello',
    });
    store.canPlanSave.set(true);
    store.canExecuteSave.set(false);
    store.mediaUrl.set(null);
    await TestBed.configureTestingModule({
      imports: [MediaPreviewDialogComponent],
      providers: [{ provide: MediaPreviewStore, useValue: store }],
    }).compileComponents();
    fixture = TestBed.createComponent(MediaPreviewDialogComponent);
    fixture.detectChanges();
  });

  afterEach(() => {
    fixture.destroy();
    vi.restoreAllMocks();
  });

  it('renders the selected same-name SRT and subtitle text as DOM text', () => {
    const dialog = fixture.nativeElement.querySelector('dialog');
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(fixture.nativeElement.textContent).toContain('/Movies/movie.srt');
    expect(fixture.nativeElement.querySelector('.subtitle-overlay').textContent).toBe('Hello');
  });

  it('offers same-directory SRT files and selects one immediately', () => {
    const picker = fixture.nativeElement.querySelector(
      '[data-testid="subtitle-picker"]',
    ) as HTMLSelectElement;

    expect([...picker.options].map((option) => option.text)).toEqual([
      'Alternate.srt',
      'movie.srt',
    ]);
    expect(picker.value).toBe('/Movies/movie.srt');

    picker.value = '/Movies/Alternate.srt';
    picker.dispatchEvent(new Event('change', { bubbles: true }));

    expect(store.selectSubtitle).toHaveBeenCalledWith('/Movies/Alternate.srt');
    expect(buttonOrNull('Load')).toBeNull();
    expect(fixture.nativeElement.querySelector('#subtitle-path[type="text"]')).toBeNull();
  });

  it('updates offset presets and the exact millisecond field', () => {
    button('+500 ms').click();
    expect(store.setOffset).toHaveBeenCalledWith(500);

    const field = fixture.nativeElement.querySelector(
      '[data-testid="offset-field"]',
    ) as HTMLInputElement;
    field.value = '-1250';
    field.dispatchEvent(new Event('input', { bubbles: true }));
    expect(store.setOffset).toHaveBeenCalledWith(-1_250);
  });

  it('seeks to the beginning, middle, and end of the video', () => {
    const video = fixture.nativeElement.querySelector('video') as HTMLVideoElement;
    Object.defineProperty(video, 'duration', { configurable: true, value: 100 });

    button('Beginning').click();
    expect(video.currentTime).toBe(0);
    button('Middle').click();
    expect(video.currentTime).toBe(50);
    button('End').click();
    expect(video.currentTime).toBeCloseTo(99.9);
  });

  it('uses Space for playback only inside the dialog', async () => {
    const video = fixture.nativeElement.querySelector('video') as HTMLVideoElement;
    const play = vi.spyOn(video, 'play').mockResolvedValue();
    const event = new KeyboardEvent('keydown', {
      key: ' ', bubbles: true, cancelable: true,
    });

    fixture.nativeElement.querySelector('dialog').dispatchEvent(event);

    expect(event.defaultPrevented).toBe(true);
    expect(play).toHaveBeenCalledOnce();
  });

  it('asks before discarding a non-zero unsaved offset on Escape', () => {
    store.state.set(readyState({ offsetMilliseconds: 500 }));
    fixture.detectChanges();
    vi.spyOn(window, 'confirm').mockReturnValue(false);

    fixture.componentInstance.handleDialogKeydown(
      new KeyboardEvent('keydown', { key: 'Escape' }),
    );
    expect(store.close).not.toHaveBeenCalled();

    vi.mocked(window.confirm).mockReturnValue(true);
    fixture.componentInstance.handleDialogKeydown(
      new KeyboardEvent('keydown', { key: 'Escape' }),
    );
    expect(store.close).toHaveBeenCalledOnce();
  });

  it('hides save execution for a read-only source', () => {
    store.state.set(readyState({
      session: { ...readyState().session!, sourceReadOnly: true },
    }));
    store.canPlanSave.set(false);
    fixture.detectChanges();

    expect(buttonOrNull('Review save')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('read-only');
  });

  it('offers explicit compatible playback after direct playback fails', () => {
    const video = fixture.nativeElement.querySelector('video') as HTMLVideoElement;

    video.dispatchEvent(new Event('error'));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Direct playback failed');
    expect(store.retryWithFallback).not.toHaveBeenCalled();
    button('Prepare compatible preview').click();
    expect(store.retryWithFallback).toHaveBeenCalledOnce();
  });

  it('shows queued work separately and lets the user cancel it', () => {
    store.state.set(readyState({
      phase: 'queued',
      session: { ...readyState().session!, phase: 'queued', playbackMode: 'hls' },
    }));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Waiting for preview worker');
    const close = fixture.nativeElement.querySelector(
      '[aria-label="Close subtitle synchronization"]',
    ) as HTMLButtonElement;
    expect(close.disabled).toBe(false);

    close.click();
    expect(store.close).toHaveBeenCalledOnce();
  });

  it('lets the user cancel an active transcode', () => {
    store.state.set(readyState({
      phase: 'transcoding',
      session: { ...readyState().session!, phase: 'transcoding', playbackMode: 'hls' },
    }));
    fixture.detectChanges();

    const close = fixture.nativeElement.querySelector(
      '[aria-label="Close subtitle synchronization"]',
    ) as HTMLButtonElement;
    expect(close.disabled).toBe(false);
    close.click();
    expect(store.close).toHaveBeenCalledOnce();
  });

  it('shows the exact original-to-backup mapping and confirms execution', () => {
    store.state.set(readyState({
      phase: 'review',
      savePlan: {
        planId: 'plan', expiresAt: '2026-09-01T10:10:00Z',
        subtitlePath: '/Movies/movie.srt', backupPath: '/Movies/movie_original.srt',
        offsetMilliseconds: 500, canExecute: true,
      },
    }));
    store.canExecuteSave.set(true);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('/Movies/movie.srt');
    expect(fixture.nativeElement.textContent).toContain('/Movies/movie_original.srt');
    expect(fixture.nativeElement.textContent).toContain('video is not modified');
    button('Save corrected subtitle').click();
    expect(store.executeSave).toHaveBeenCalledOnce();
  });

  function button(label: string): HTMLButtonElement {
    return buttonOrNull(label) ?? null as never;
  }

  function buttonOrNull(label: string): HTMLButtonElement | null {
    return [...fixture.nativeElement.querySelectorAll('button')]
      .find((candidate: HTMLButtonElement) => candidate.textContent?.trim() === label) ?? null;
  }
});

function readyState(overrides: Partial<MediaPreviewState> = {}): MediaPreviewState {
  return {
    phase: 'ready',
    context: {
      sourceId: 'media', videoPath: '/Movies/movie.mp4',
      videoName: 'movie.mp4', sourceReadOnly: false,
    },
    session: {
      sessionId: 'session', phase: 'ready', playbackMode: 'direct', videoName: 'movie.mp4',
      videoPath: '/Movies/movie.mp4', durationMilliseconds: 100_000,
      subtitlePath: '/Movies/movie.srt',
      cues: [{ index: 0, startMilliseconds: 1_000, endMilliseconds: 2_000, text: 'Hello' }],
      sourceReadOnly: false, expiresAt: '2026-09-01T10:20:00Z',
      failureCode: null, failureDetail: null, transcodeActive: false,
    },
    subtitleCandidates: [
      { name: 'Alternate.srt', path: '/Movies/Alternate.srt' },
      { name: 'movie.srt', path: '/Movies/movie.srt' },
    ],
    offsetMilliseconds: 0,
    videoTimeMilliseconds: 0,
    savePlan: null,
    saveResult: null,
    error: null,
    requestToken: 1,
    ...overrides,
  };
}
