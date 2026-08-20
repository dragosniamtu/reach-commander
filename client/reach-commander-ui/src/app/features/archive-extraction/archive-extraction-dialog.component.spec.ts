import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ArchiveExtractionStore, ArchiveExtractionState } from '../../core/state/archive-extraction-store';
import { ArchiveExtractionDialogComponent } from './archive-extraction-dialog.component';

describe('ArchiveExtractionDialogComponent', () => {
  let fixture: ComponentFixture<ArchiveExtractionDialogComponent>;
  const store = {
    state: signal<ArchiveExtractionState>(reviewState()),
    canExecute: signal(true),
    canCancel: signal(false),
    execute: vi.fn(() => Promise.resolve()),
    cancel: vi.fn(() => Promise.resolve()),
    reviewAgain: vi.fn(() => Promise.resolve()),
    close: vi.fn(),
  };

  beforeEach(async () => {
    vi.clearAllMocks();
    store.state.set(reviewState());
    store.canExecute.set(true);
    store.canCancel.set(false);
    await TestBed.configureTestingModule({
      imports: [ArchiveExtractionDialogComponent],
      providers: [{ provide: ArchiveExtractionStore, useValue: store }],
    }).compileComponents();
    fixture = TestBed.createComponent(ArchiveExtractionDialogComponent);
    fixture.detectChanges();
  });

  it('renders review metadata, unknown totals, and blocks extraction on issues', () => {
    store.state.set(reviewState({
      preview: {
        ...reviewState().preview!,
        totalExtractedBytes: null,
        canExecute: false,
        conflicts: [{ code: 'archive_destination_conflict', message: 'Already exists.', logicalPaths: ['2025'] }],
      },
    }));
    store.canExecute.set(false);
    fixture.detectChanges();

    const dialog = fixture.nativeElement.querySelector('dialog');
    expect(dialog.getAttribute('aria-labelledby')).toBe('archive-extraction-title');
    expect(dialog.textContent).toContain('/photos.7z');
    expect(dialog.textContent).toContain('7-Zip');
    expect(dialog.textContent).toContain('2 volumes');
    expect(dialog.textContent).toContain('Unknown');
    expect(dialog.textContent).toContain('Media:/Photos');
    expect(dialog.textContent).toContain('Already exists.');
    expect(button('Extract').disabled).toBe(true);
  });

  it('opens as a modal with a focus target and reports a phase-aware status', () => {
    const dialog = fixture.nativeElement.querySelector('dialog') as HTMLDialogElement;
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(dialog.tabIndex).toBe(-1);
    expect(fixture.nativeElement.querySelector('footer [aria-live="polite"]').textContent)
      .toContain('Ready to extract');

    store.state.set({ ...reviewState(), phase: 'previewing', preview: null });
    fixture.detectChanges();
    dialog.focus();
    expect(document.activeElement).toBe(dialog);
    expect(fixture.nativeElement.querySelector('footer [aria-live="polite"]').textContent)
      .toContain('Inspecting archive');
  });

  it('keeps Tab inside the dialog instead of dispatching it to commander shortcuts', () => {
    const documentKeydown = vi.fn();
    document.addEventListener('keydown', documentKeydown);
    const tab = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true });

    fixture.nativeElement.querySelector('dialog').dispatchEvent(tab);

    expect(documentKeydown).not.toHaveBeenCalled();
    expect(tab.defaultPrevented).toBe(false);
    document.removeEventListener('keydown', documentKeydown);
  });

  it('shows determinate progress only when total and percent are known', () => {
    store.state.set(runningState());
    store.canCancel.set(true);
    fixture.detectChanges();

    const progress = fixture.nativeElement.querySelector('progress') as HTMLProgressElement;
    expect(progress.value).toBe(50);
    expect(fixture.nativeElement.textContent).toContain('photo.jpg');
    expect(button('Cancel extraction')).not.toBeNull();

    store.state.set(runningState({ operation: { ...runningState().operation!, totalBytes: null, percent: null } }));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('progress')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="indeterminate-progress"]')).not.toBeNull();
  });

  it('requires a fresh review after a stale execution preview', () => {
    store.state.set(reviewState({
      error: { code: 'archive_plan_stale', detail: 'The archive changed after preview.' },
    }));
    store.canExecute.set(false);
    fixture.detectChanges();

    expect(button('Extract')).toBeNull();
    button('Review again').click();
    expect(store.reviewAgain).toHaveBeenCalledOnce();
  });

  it('locks cancellation while finalizing and renders terminal recovery guidance', () => {
    store.state.set(runningState({ operation: { ...runningState().operation!, state: 'finalizing', canCancel: false } }));
    fixture.detectChanges();
    expect(button('Cancel extraction')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Finalizing');

    store.state.set({
      ...runningState(),
      phase: 'recoveryRequired',
      operation: {
        ...runningState().operation!,
        state: 'recoveryRequired',
        canCancel: false,
        recoveryNames: ['safe.partial'],
        compensationState: 'failed',
        errorCode: 'archive_recovery_required',
        errorDetail: 'Administrator recovery is required.',
      },
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('safe.partial');
    expect(fixture.nativeElement.textContent).toContain('Do not delete');
    expect(fixture.nativeElement.querySelector('[aria-live="assertive"]')).not.toBeNull();
  });

  it('closes review on Escape but requests cancellation instead of dismissing running work', () => {
    const close = vi.fn();
    fixture.componentInstance.closeRequested.subscribe(close);
    fixture.componentInstance.handleDialogKeydown(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(close).toHaveBeenCalledOnce();

    close.mockClear();
    store.state.set(runningState());
    store.canCancel.set(true);
    fixture.detectChanges();
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    fixture.componentInstance.handleDialogKeydown(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(store.cancel).toHaveBeenCalledOnce();
    expect(close).not.toHaveBeenCalled();
  });

  it('stops the extraction lifecycle when the dialog is destroyed', () => {
    fixture.destroy();
    expect(store.close).toHaveBeenCalledOnce();
  });

  function button(label: string): HTMLButtonElement {
    return [...fixture.nativeElement.querySelectorAll('button')]
      .find((candidate: HTMLButtonElement) => candidate.textContent?.trim() === label) ?? null as never;
  }
});

function reviewState(overrides: Partial<ArchiveExtractionState> = {}): ArchiveExtractionState {
  return {
    phase: 'review',
    context: {
      sourcePanelSide: 'left', destinationPanelSide: 'right', sourceId: 'downloads',
      sourceName: 'Downloads', archivePath: '/photos.7z', internalDirectory: '/Family',
      entryPaths: ['/Family/2025'], extractAll: false, format: 'sevenZip', volumeCount: 2,
      destinationSourceId: 'media', destinationSourceName: 'Media', destinationPath: '/Photos',
    },
    preview: {
      planId: 'plan', expiresAt: '2026-08-20T08:10:00Z', format: 'sevenZip', volumeCount: 2,
      selectedRoots: ['2025'], fileCount: 1, directoryCount: 1, totalExtractedBytes: 12,
      destinationSourceId: 'media', destinationPath: '/Photos', conflicts: [], violations: [],
      canExecute: true,
    },
    operation: null,
    error: null,
    requestToken: 1,
    ...overrides,
  };
}

function runningState(overrides: Partial<ArchiveExtractionState> = {}): ArchiveExtractionState {
  return {
    ...reviewState(),
    phase: 'running',
    operation: {
      operationId: 'operation', state: 'extracting', completedFiles: 1, totalFiles: 2,
      extractedBytes: 6, totalBytes: 12, percent: 50, currentEntryName: 'photo.jpg',
      canCancel: true, compensationState: 'notRequired', recoveryNames: [], errorCode: null,
      errorDetail: null,
    },
    ...overrides,
  };
}
