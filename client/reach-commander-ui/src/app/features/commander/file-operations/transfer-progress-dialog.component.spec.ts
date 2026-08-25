import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FileOperationStatusDto } from '../../../core/api/api.models';
import { FileOperationStore } from './file-operation.store';
import { TransferProgressDialogComponent } from './transfer-progress-dialog.component';

describe('TransferProgressDialogComponent', () => {
  let fixture: ComponentFixture<TransferProgressDialogComponent>;
  const store = fakeStore();

  beforeEach(async () => {
    vi.clearAllMocks();
    store.activeTask.set(task());
    await TestBed.configureTestingModule({
      imports: [TransferProgressDialogComponent],
      providers: [{ provide: FileOperationStore, useValue: store }],
    }).compileComponents();
    fixture = TestBed.createComponent(TransferProgressDialogComponent);
    fixture.detectChanges();
  });

  it('blocks the workspace and shows determinate progress details', () => {
    const dialog = fixture.nativeElement.querySelector('dialog');
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(fixture.nativeElement.querySelector('.transfer-backdrop')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('progress').value).toBe(42);
    expect(fixture.nativeElement.textContent).toContain('42%');
    expect(fixture.nativeElement.textContent).toContain('2.0 KiB/s');
    expect(fixture.nativeElement.textContent).toContain('00:00:12');
    expect(fixture.nativeElement.textContent).toContain('00:00:20');
  });

  it('backgrounds progress and delegates cancellation', () => {
    button('Background').click();
    button('Cancel copy').click();
    expect(store.background).toHaveBeenCalledOnce();
    expect(store.cancel).toHaveBeenCalledWith('operation-id');
  });

  it('uses indeterminate progress for unknown totals and acknowledges terminal results', () => {
    store.activeTask.set(task({
      phase: 'completed',
      progress: { ...task().progress, percentage: null, totalBytes: null },
    }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('progress')).toBeNull();
    expect(fixture.nativeElement.querySelector('[role="progressbar"]')).not.toBeNull();
    button('Close').click();
    expect(store.acknowledge).toHaveBeenCalledWith('operation-id');
  });

  function button(label: string): HTMLButtonElement {
    return [...fixture.nativeElement.querySelectorAll('button')]
      .find((candidate: HTMLButtonElement) => candidate.textContent?.trim() === label)!;
  }
});

function fakeStore() {
  return {
    activeTask: signal<FileOperationStatusDto | null>(task()),
    background: vi.fn(),
    cancel: vi.fn(),
    acknowledge: vi.fn(),
  };
}

function task(overrides: Partial<FileOperationStatusDto> = {}): FileOperationStatusDto {
  return {
    operationId: 'operation-id', kind: 'copy', phase: 'running', queuePosition: 0,
    createdAt: '2026-08-25T09:00:00Z', updatedAt: '2026-08-25T09:00:12Z',
    progress: {
      currentLogicalName: 'movie.mkv', completedItems: 2, totalItems: 5,
      completedBytes: 420, totalBytes: 1000, percentage: 42, bytesPerSecond: 2048,
      elapsed: '00:00:12', estimatedRemaining: '00:00:20',
    },
    outcomes: [], warnings: [], acknowledged: false, ...overrides,
  };
}
