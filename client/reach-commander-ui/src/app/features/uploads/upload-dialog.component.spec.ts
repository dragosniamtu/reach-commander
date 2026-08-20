import { computed, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { UploadStore } from '../../core/state/upload-store';
import { UploadState } from '../../core/state/upload.models';
import { UploadDialogComponent } from './upload-dialog.component';

describe('UploadDialogComponent', () => {
  let fixture: ComponentFixture<UploadDialogComponent>;
  let store: FakeUploadStore;
  let opener: HTMLButtonElement;

  beforeEach(async () => {
    store = new FakeUploadStore();
    await TestBed.configureTestingModule({
      imports: [UploadDialogComponent],
      providers: [{ provide: UploadStore, useValue: store }],
    }).compileComponents();
    opener = document.createElement('button');
    document.body.appendChild(opener);
    opener.focus();
    fixture = TestBed.createComponent(UploadDialogComponent);
    fixture.componentRef.setInput('opener', opener);
    fixture.detectChanges();
    await fixture.whenStable();
  });

  afterEach(() => opener.remove());

  it('shows the captured destination, selected files, total, and effective limits', () => {
    const text = screenText();

    expect(text).toContain('Add files');
    expect(text).toContain('Media');
    expect(text).toContain('/Movies');
    expect(text).toContain('2 files');
    expect(text).toContain('one.txt');
    expect(text).toContain('empty.bin');
    expect(text).toContain('3 B');
    expect(text).toContain('8 B per file');
    expect(text).toContain('12 B per batch');
    expect(text).toContain('2 files per batch');
  });

  it('removes review rows and disables the primary action for preflight failures', () => {
    store.setState({
      ...reviewState(),
      preflightIssues: [
        {
          code: 'upload_file_too_large',
          message: 'large.bin exceeds the per-file upload limit.',
          fileName: 'large.bin',
        },
      ],
    });
    fixture.detectChanges();

    const removeButtons = fixture.nativeElement.querySelectorAll(
      '[data-testid="remove-upload-file"]',
    );
    (removeButtons[0] as HTMLButtonElement).click();

    expect(store.removeFile).toHaveBeenCalledWith(0);
    expect(primaryButton().disabled).toBe(true);
    expect(screenText()).toContain('large.bin exceeds the per-file upload limit.');
  });

  it('starts from review and renders determinate upload progress with cancellation', () => {
    primaryButton().click();
    expect(store.start).toHaveBeenCalledOnce();

    store.setState({
      ...reviewState(),
      phase: 'uploading',
      progressLoadedBytes: 2,
      progressTotalBytes: 4,
    });
    fixture.detectChanges();
    const progress = fixture.nativeElement.querySelector('progress') as HTMLProgressElement;

    expect(progress.value).toBe(2);
    expect(progress.max).toBe(4);
    expect(progress.getAttribute('aria-label')).toBe('Upload progress');
    expect(screenText()).toContain('2 B of 4 B');

    (
      fixture.nativeElement.querySelector('[data-testid="cancel-upload"]') as HTMLButtonElement
    ).click();
    expect(store.cancel).toHaveBeenCalledOnce();
  });

  it('keeps finalization non-dismissible and renders safe failure recovery', () => {
    store.setState({ ...reviewState(), phase: 'finalizing' });
    fixture.detectChanges();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    (fixture.nativeElement.querySelector('.upload-backdrop') as HTMLElement).click();

    expect(store.close).not.toHaveBeenCalled();
    expect(screenText()).toContain('Finalizing safely');

    store.setState({
      ...reviewState(),
      phase: 'failed',
      errorCode: 'upload_name_conflict',
      errorMessage: 'One or more files already exist in this folder.',
    });
    fixture.detectChanges();

    expect(screenText()).toContain('One or more files already exist in this folder.');
    expect(screenText()).toContain('one.txt');
    expect(primaryButton().textContent).toContain('Try again');
  });

  it('renders the completed logical result without physical paths', () => {
    store.setState({
      ...reviewState(),
      phase: 'completed',
      result: {
        uploadedCount: 2,
        totalBytes: 3,
        files: [
          { name: 'one.txt', relativePath: '/Movies/one.txt', size: 3 },
          { name: 'empty.bin', relativePath: '/Movies/empty.bin', size: 0 },
        ],
      },
    });
    fixture.detectChanges();

    expect(screenText()).toContain('Upload complete');
    expect(screenText()).toContain('/Movies/one.txt');
    expect(screenText()).not.toContain('D:\\');
  });

  it('is a named focus-trapped modal and restores focus when Escape closes it', () => {
    const dialog = fixture.nativeElement.querySelector('[role="dialog"]');
    const anchors = fixture.nativeElement.querySelectorAll('.cdk-focus-trap-anchor');

    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(dialog.getAttribute('aria-labelledby')).toBe('upload-dialog-title');
    expect(anchors).toHaveLength(2);
    expect(document.activeElement).toBe(primaryButton());

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));

    expect(store.close).toHaveBeenCalledOnce();
    expect(document.activeElement).toBe(opener);
  });

  function primaryButton(): HTMLButtonElement {
    return fixture.nativeElement.querySelector('[data-testid="upload-primary"]');
  }

  function screenText(): string {
    return fixture.nativeElement.textContent.replace(/\s+/g, ' ');
  }
});

class FakeUploadStore {
  private readonly mutableState = signal<UploadState>(reviewState());
  readonly state = this.mutableState.asReadonly();
  readonly isPending = computed(() => ['uploading', 'finalizing'].includes(this.state().phase));
  readonly removeFile = vi.fn();
  readonly start = vi.fn(() => true);
  readonly cancel = vi.fn(() => true);
  readonly close = vi.fn(() => true);

  setState(state: UploadState): void {
    this.mutableState.set(state);
  }
}

function reviewState(): UploadState {
  return {
    phase: 'review',
    context: {
      side: 'left',
      sourceId: 'media',
      sourceName: 'Media',
      directoryPath: '/Movies',
    },
    files: [new File(['one'], 'one.txt'), new File([], 'empty.bin')],
    limits: { maxFileBytes: 8, maxBatchBytes: 12, maxFilesPerBatch: 2 },
    limitsPending: false,
    totalBytes: 3,
    preflightIssues: [],
    progressLoadedBytes: 0,
    progressTotalBytes: null,
    result: null,
    errorCode: null,
    errorMessage: null,
    requestToken: 1,
  };
}
