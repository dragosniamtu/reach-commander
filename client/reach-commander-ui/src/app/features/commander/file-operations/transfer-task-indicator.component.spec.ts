import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { FileOperationStatusDto } from '../../../core/api/api.models';
import { FileOperationStore } from './file-operation.store';
import { TransferTaskIndicatorComponent } from './transfer-task-indicator.component';

describe('TransferTaskIndicatorComponent', () => {
  let fixture: ComponentFixture<TransferTaskIndicatorComponent>;
  const first = task();
  const store = {
    activeTask: signal<FileOperationStatusDto | null>(first),
    tasks: signal<readonly FileOperationStatusDto[]>([first, task({ operationId: 'queued', phase: 'queued' })]),
    queuedCount: signal(1),
    restoreProgress: vi.fn(),
  };

  beforeEach(async () => {
    vi.clearAllMocks();
    await TestBed.configureTestingModule({
      imports: [TransferTaskIndicatorComponent],
      providers: [{ provide: FileOperationStore, useValue: store }],
    }).compileComponents();
    fixture = TestBed.createComponent(TransferTaskIndicatorComponent);
    fixture.detectChanges();
  });

  it('shows one compact accessible progress summary and queued count', () => {
    const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    expect(button.textContent).toContain('Copy');
    expect(button.textContent).toContain('42%');
    expect(button.textContent).toContain('+1 queued');
    expect(button.getAttribute('aria-label')).toContain('Copy 42%');
  });

  it('restores the selected task modal when clicked', () => {
    fixture.nativeElement.querySelector('button').click();
    expect(store.restoreProgress).toHaveBeenCalledWith('operation-id');
  });
});

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
