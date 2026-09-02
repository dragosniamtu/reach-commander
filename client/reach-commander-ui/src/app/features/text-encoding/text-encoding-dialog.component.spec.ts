import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TextEncodingState, TextEncodingStore } from '../../core/state/text-encoding-store';
import { TextEncodingDialogComponent } from './text-encoding-dialog.component';

describe('TextEncodingDialogComponent', () => {
  let fixture: ComponentFixture<TextEncodingDialogComponent>;
  const store = {
    state: signal<TextEncodingState>(reviewState()),
    canExecute: signal(true),
    canCancel: signal(false),
    setSourceEncoding: vi.fn(),
    setOutputEncoding: vi.fn(),
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
      imports: [TextEncodingDialogComponent],
      providers: [{ provide: TextEncodingStore, useValue: store }],
    }).compileComponents();
    fixture = TestBed.createComponent(TextEncodingDialogComponent);
    fixture.detectChanges();
  });

  it('opens a trapped full-screen dialog and initially focuses the source encoding', async () => {
    await fixture.whenStable();
    const dialog = fixture.nativeElement.querySelector('dialog') as HTMLDialogElement;
    const source = fixture.nativeElement.querySelector('#text-encoding-source') as HTMLSelectElement;

    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(dialog.getAttribute('role')).toBe('dialog');
    expect(dialog.getAttribute('aria-labelledby')).toBe('text-encoding-title');
    expect(source).not.toBeNull();
    expect(document.activeElement).toBe(source);
    expect([...source.options].map((option) => option.text)).toEqual([
      'Auto detect', 'UTF-8', 'UTF-8 with BOM', 'UTF-16 LE', 'UTF-16 BE',
      'Windows-1250', 'Windows-1252',
    ]);
  });

  it('renders a safe preview table, warnings, and exact backup policy', () => {
    store.state.set(reviewState({
      preview: {
        ...reviewState().preview!,
        warningCount: 1,
        rows: [{
          filePath: '/captions.srt',
          fileName: 'captions.srt',
          detectedSourceEncoding: 'windows1250',
          confidence: 'low',
          status: 'warning',
          code: 'text_encoding_ambiguous',
          detail: 'Legacy encoding is ambiguous.',
          previewText: '<img src=x onerror=alert(1)>',
        }],
      },
    }));
    fixture.detectChanges();

    const preview = fixture.nativeElement.querySelector('[data-testid="text-encoding-preview"]');
    expect(preview.textContent).toContain('captions.srt');
    expect(preview.textContent).toContain('Windows-1250');
    expect(preview.textContent).toContain('Legacy encoding is ambiguous.');
    expect(preview.textContent).toContain('<img src=x onerror=alert(1)>');
    expect(preview.querySelector('img')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('captions_original.srt');
  });

  it('updates selectors and starts a reviewed conversion', () => {
    const source = fixture.nativeElement.querySelector('#text-encoding-source') as HTMLSelectElement;
    const output = fixture.nativeElement.querySelector('#text-encoding-output') as HTMLSelectElement;
    source.value = 'windows1252';
    source.dispatchEvent(new Event('change'));
    output.value = 'utf8Bom';
    output.dispatchEvent(new Event('change'));
    button('Convert files').click();

    expect(store.setSourceEncoding).toHaveBeenCalledWith('windows1252');
    expect(store.setOutputEncoding).toHaveBeenCalledWith('utf8Bom');
    expect(store.execute).toHaveBeenCalledOnce();
  });

  it('announces per-file progress and asks before cancelling active work with Escape', () => {
    store.state.set(runningState());
    store.canExecute.set(false);
    store.canCancel.set(true);
    fixture.detectChanges();

    const progress = fixture.nativeElement.querySelector('progress') as HTMLProgressElement;
    expect(progress.value).toBe(50);
    expect(fixture.nativeElement.querySelector('[aria-live="polite"]').textContent)
      .toContain('1 / 2 · captions.srt');

    vi.spyOn(window, 'confirm').mockReturnValue(true);
    fixture.componentInstance.handleDialogKeydown(
      new KeyboardEvent('keydown', { key: 'Escape', cancelable: true }),
    );
    expect(window.confirm).toHaveBeenCalledWith('Cancel the text encoding operation?');
    expect(store.cancel).toHaveBeenCalledOnce();
  });

  it('requires recovery acknowledgement before closing a recovery result', () => {
    const close = vi.fn();
    fixture.componentInstance.closeRequested.subscribe(close);
    store.state.set(runningState({
      phase: 'completedWithErrors',
      operation: {
        ...runningState().operation!,
        state: 'completedWithErrors',
        canCancel: false,
        rows: [{
          filePath: '/captions.srt', backupPath: '/captions_original.srt',
          result: 'recoveryRequired', code: 'text_encoding_recovery_required',
          detail: 'Inspect the original and staging file.',
        }],
      },
    }));
    store.canCancel.set(false);
    fixture.detectChanges();

    expect(button('Close').disabled).toBe(true);
    const acknowledgement = fixture.nativeElement.querySelector(
      '[data-testid="text-encoding-recovery-acknowledgement"]',
    ) as HTMLInputElement;
    acknowledgement.click();
    fixture.detectChanges();
    expect(button('Close').disabled).toBe(false);
    button('Close').click();
    expect(close).toHaveBeenCalledOnce();
  });

  it('closes review on Escape and stops the lifecycle when destroyed', () => {
    const close = vi.fn();
    fixture.componentInstance.closeRequested.subscribe(close);
    fixture.componentInstance.handleDialogKeydown(
      new KeyboardEvent('keydown', { key: 'Escape', cancelable: true }),
    );
    expect(close).toHaveBeenCalledOnce();

    fixture.destroy();
    expect(store.close).toHaveBeenCalledOnce();
  });

  function button(label: string): HTMLButtonElement {
    return [...fixture.nativeElement.querySelectorAll('button')]
      .find((candidate: HTMLButtonElement) => candidate.textContent?.trim() === label) ?? null as never;
  }
});

function reviewState(overrides: Partial<TextEncodingState> = {}): TextEncodingState {
  return {
    phase: 'review',
    context: {
      panelSide: 'left', sourceId: 'media', sourceName: 'Media', directoryPath: '/TV',
      entries: [],
    },
    sourceEncoding: 'auto',
    outputEncoding: 'utf8',
    preview: {
      planId: 'plan', expiresAt: '2099-09-02T13:31:43Z', readyCount: 1,
      warningCount: 0, invalidCount: 0, canExecute: true,
      rows: [{
        filePath: '/captions.srt', fileName: 'captions.srt', detectedSourceEncoding: 'utf8',
        confidence: 'high', status: 'ready', code: null, detail: null,
        previewText: 'A preview line',
      }],
    },
    operation: null,
    error: null,
    requestToken: 1,
    ...overrides,
  };
}

function runningState(overrides: Partial<TextEncodingState> = {}): TextEncodingState {
  return {
    ...reviewState(),
    phase: 'running',
    preview: null,
    operation: {
      operationId: 'operation', state: 'running', completedFiles: 1, totalFiles: 2,
      percent: 50, currentFileName: 'captions.srt', canCancel: true,
      rows: [], errorCode: null, errorDetail: null,
    },
    ...overrides,
  };
}
