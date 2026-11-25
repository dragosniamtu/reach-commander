import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SubtitleGenerationDialogComponent } from './subtitle-generation-dialog.component';
import { SubtitleGenerationStore } from './subtitle-generation.store';

describe('SubtitleGenerationDialogComponent', () => {
  let fixture: ComponentFixture<SubtitleGenerationDialogComponent>;
  let store: SubtitleGenerationStore;
  let http: HttpTestingController;
  const ready = {
    enabled: true,
    workerAvailable: true,
    englishAvailable: true,
    romanianAvailable: true,
    reasonCode: null,
    detail: null,
  };
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SubtitleGenerationDialogComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    store = TestBed.inject(SubtitleGenerationStore);
    http = TestBed.inject(HttpTestingController);
    store.open({ panelSide: 'left', sourceId: 'media', sourceName: 'Media', path: '/Movie.mkv' });
    http.expectOne('/api/subtitle-jobs/capabilities').flush(ready);
    await Promise.resolve();
    fixture = TestBed.createComponent(SubtitleGenerationDialogComponent);
    fixture.detectChanges();
  });
  afterEach(() => http.verify());

  it.each([
    [
      { ...ready, enabled: false, reasonCode: 'subtitle_generation_disabled' },
      'disabled on this server',
    ],
    [
      { ...ready, workerAvailable: false, reasonCode: 'subtitle_worker_unavailable' },
      'worker is offline',
    ],
    [
      {
        ...ready,
        englishAvailable: false,
        romanianAvailable: false,
        reasonCode: 'subtitle_models_missing',
      },
      'No subtitle language models are ready',
    ],
    [{ ...ready, reasonCode: 'subtitle_state_unavailable' }, 'readiness could not be checked'],
  ])(
    'explains unavailable generation and allows a readiness recheck (%j)',
    async (capabilities, message) => {
      store.open({ panelSide: 'left', sourceId: 'media', sourceName: 'Media', path: '/Movie.mkv' });
      fixture.detectChanges();
      expect(fixture.nativeElement.textContent).toContain('Checking subtitle availability');
      http.expectOne('/api/subtitle-jobs/capabilities').flush(capabilities);
      await Promise.resolve();
      fixture.detectChanges();
      expect(fixture.nativeElement.textContent).toContain(message);
      expect(
        fixture.nativeElement.querySelector('[data-testid="subtitle-generate"]').disabled,
      ).toBe(true);
      fixture.nativeElement.querySelector('[data-testid="subtitle-recheck"]').click();
      http.expectOne('/api/subtitle-jobs/capabilities').flush(ready);
      await Promise.resolve();
      fixture.detectChanges();
      expect(
        fixture.nativeElement.querySelector('[data-testid="subtitle-generate"]').disabled,
      ).toBe(false);
    },
  );

  it('keeps English available when Romanian models are missing', async () => {
    store.open({ panelSide: 'left', sourceId: 'media', sourceName: 'Media', path: '/Movie.mkv' });
    http.expectOne('/api/subtitle-jobs/capabilities').flush({ ...ready, romanianAvailable: false });
    await Promise.resolve();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Romanian is not set up');
    expect(fixture.nativeElement.querySelector('#subtitle-english').disabled).toBe(false);
    expect(fixture.nativeElement.querySelector('#subtitle-romanian').disabled).toBe(true);
    expect(fixture.nativeElement.querySelector('[data-testid="subtitle-generate"]').disabled).toBe(
      false,
    );
  });

  it('shows English checked, Romanian unchecked, and validates at least one choice', () => {
    const english = fixture.nativeElement.querySelector('#subtitle-english') as HTMLInputElement;
    const romanian = fixture.nativeElement.querySelector('#subtitle-romanian') as HTMLInputElement;
    const generate = fixture.nativeElement.querySelector(
      '[data-testid="subtitle-generate"]',
    ) as HTMLButtonElement;
    expect(english.checked).toBe(true);
    expect(romanian.checked).toBe(false);
    english.click();
    fixture.detectChanges();
    expect(generate.disabled).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('Select at least one language.');
    romanian.click();
    fixture.detectChanges();
    expect(generate.disabled).toBe(false);
  });

  it('closes with Escape without cancelling a server job or leaking keyboard events', () => {
    const close = vi.fn();
    fixture.componentInstance.closeRequested.subscribe(close);
    const event = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true });
    const stop = vi.spyOn(event, 'stopPropagation');
    fixture.componentInstance.handleKeydown(event);
    expect(close).toHaveBeenCalledOnce();
    expect(stop).toHaveBeenCalled();
    expect(event.defaultPrevented).toBe(true);
  });

  it('renders current and overall progress, retained failures, skipped files, and cancellation', async () => {
    store.openHistory();
    http.expectOne('/api/subtitle-jobs/capabilities').flush(ready);
    http.expectOne('/api/subtitle-jobs').flush([
      {
        jobId: 'job-1',
        sourceId: 'media',
        path: '/TV',
        english: true,
        romanian: true,
        phase: 'running',
        stage: 'transcribing',
        currentPath: '/TV/Four.mkv',
        currentPercent: 35,
        files: [
          { path: '/TV/One.mkv', phase: 'completed', error: null },
          { path: '/TV/Two.mkv', phase: 'skipped', error: null },
          { path: '/TV/Three.mkv', phase: 'failed', error: 'No audio stream.' },
          { path: '/TV/Four.mkv', phase: 'running', error: null },
        ],
        createdAt: '2026-11-24T12:00:00Z',
        updatedAt: '2026-11-24T12:01:00Z',
        error: null,
      },
    ]);
    await Promise.resolve();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('3 of 4 files processed');
    expect(fixture.nativeElement.textContent).toContain('1 completed · 1 skipped · 1 failed');
    expect(fixture.nativeElement.textContent).toContain('No audio stream.');
    expect(fixture.nativeElement.textContent).toContain('/TV/Four.mkv');
    expect(fixture.nativeElement.querySelector('[aria-label="Current video progress"]').value).toBe(
      35,
    );
    const cancel = vi.spyOn(store, 'cancel').mockResolvedValue(undefined);
    fixture.nativeElement.querySelector('[data-testid="subtitle-cancel-job"]').click();
    expect(cancel).toHaveBeenCalledWith('job-1');
    http.verify();
  });
});
