import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { BatchRenameOperationDto, BatchRenamePreviewDto } from '../../core/api/api.models';
import { MultiRenameState } from '../../core/state/multi-rename.models';
import { MultiRenameStore } from '../../core/state/multi-rename-store';
import { MultiRenameDialogComponent } from './multi-rename-dialog.component';

describe('MultiRenameDialogComponent', () => {
  let fixture: ComponentFixture<MultiRenameDialogComponent>;
  const fakeStore = {
    state: signal<MultiRenameState>(openState()),
    canExecute: signal(false),
    canUndo: signal(false),
    updateRules: vi.fn(),
    execute: vi.fn(() => Promise.resolve(true)),
    undo: vi.fn(() => Promise.resolve(true)),
  };

  beforeEach(async () => {
    vi.clearAllMocks();
    fakeStore.state.set(openState());
    fakeStore.canExecute.set(false);
    fakeStore.canUndo.set(false);
    await TestBed.configureTestingModule({
      imports: [MultiRenameDialogComponent],
      providers: [{ provide: MultiRenameStore, useValue: fakeStore }],
    }).compileComponents();
    fixture = TestBed.createComponent(MultiRenameDialogComponent);
  });

  it('renders dense rule controls, complete preview, and disabled Start state', () => {
    fakeStore.state.set(openState({ preview: previewResponse({ canExecute: false }) }));
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[role="dialog"]')?.getAttribute('aria-modal')).toBe('true');
    expect(root.querySelector('[data-testid="name-mask"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="extension-mask"]')).toBeTruthy();
    expect(root.querySelector('[data-testid="multi-rename-preview"]')).toBeTruthy();
    expect(root.textContent).toContain('Archive-001.txt');
    expect((root.querySelector('[data-testid="rename-start"]') as HTMLButtonElement).disabled).toBe(
      true,
    );
  });

  it('delegates immutable rule edits to the store', () => {
    fixture.detectChanges();
    const input = fixture.nativeElement.querySelector(
      '[data-testid="name-mask"]',
    ) as HTMLInputElement;
    input.value = 'Trip-[C]';
    input.dispatchEvent(new Event('input'));

    expect(fakeStore.updateRules).toHaveBeenCalledWith({ nameMask: 'Trip-[C]' });
  });

  it('executes only an authoritative plan and exposes Undo after success', async () => {
    fakeStore.state.set(openState({ preview: previewResponse({ canExecute: true }) }));
    fakeStore.canExecute.set(true);
    fakeStore.execute.mockImplementationOnce(async () => {
      fakeStore.state.update((state) => ({ ...state, operation: operationResponse() }));
      fakeStore.canUndo.set(true);
      return true;
    });
    const changed = vi.fn();
    fixture.componentInstance.filesystemChanged.subscribe(changed);
    fixture.detectChanges();

    (
      fixture.nativeElement.querySelector('[data-testid="rename-start"]') as HTMLButtonElement
    ).click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fakeStore.execute).toHaveBeenCalledOnce();
    expect(fixture.nativeElement.textContent).toContain('2 entries renamed');
    expect(
      (fixture.nativeElement.querySelector('[data-testid="rename-undo"]') as HTMLButtonElement)
        .disabled,
    ).toBe(false);
    expect(changed).toHaveBeenCalledWith('left');
  });

  it('blocks close while pending and requires acknowledgement for recovery-required results', () => {
    fakeStore.state.set(
      openState({
        operation: operationResponse({ status: 'recoveryRequired', recoveryRequired: true }),
      }),
    );
    fixture.detectChanges();

    expect(
      (
        fixture.nativeElement.querySelector(
          '[aria-label="Close Multi-Rename"]',
        ) as HTMLButtonElement
      ).disabled,
    ).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('Recovery required');
    expect(fixture.nativeElement.textContent).toContain('/Movies/current-a.txt');
  });
});

function openState(overrides: Partial<MultiRenameState> = {}): MultiRenameState {
  return {
    open: true,
    context: {
      panelSide: 'left',
      sourceId: 'media',
      sourceName: 'Media',
      directoryPath: '/Movies',
      entries: [],
      isAvailable: true,
      isReadOnly: false,
    },
    rules: {
      nameMask: '[N]',
      extensionMask: '[E]',
      searchFor: '',
      replaceWith: '',
      useRegex: false,
      matchCase: false,
      replaceInExtension: false,
      caseMode: 'unchanged',
      counterStart: 1,
      counterStep: 1,
      counterDigits: 1,
    },
    preview: previewResponse(),
    operation: null,
    previewPending: false,
    actionPending: false,
    disabledReason: null,
    errorCode: null,
    requestToken: 1,
    ...overrides,
  };
}

function previewResponse(overrides: Partial<BatchRenamePreviewDto> = {}): BatchRenamePreviewDto {
  return {
    planId: 'plan',
    expiresAt: '2026-08-20T08:10:00Z',
    rows: [
      {
        sourcePath: '/Movies/holiday.txt',
        oldName: 'holiday.txt',
        oldExtension: 'txt',
        newName: 'Archive-001.txt',
        type: 'file',
        size: 1,
        modifiedAt: '2026-08-20T08:00:00Z',
        status: 'ready',
        message: null,
      },
    ],
    canExecute: true,
    changedCount: 1,
    unchangedCount: 0,
    invalidCount: 0,
    ...overrides,
  };
}

function operationResponse(
  overrides: Partial<BatchRenameOperationDto> = {},
): BatchRenameOperationDto {
  return {
    operationId: 'operation',
    status: 'completed',
    rows: [
      {
        oldPath: '/Movies/a.txt',
        newPath: '/Movies/one.txt',
        currentPath: '/Movies/current-a.txt',
        oldName: 'a.txt',
        newName: 'one.txt',
        currentName: 'current-a.txt',
        type: 'file',
        result: 'completed',
        message: null,
      },
      {
        oldPath: '/Movies/b.txt',
        newPath: '/Movies/two.txt',
        currentPath: '/Movies/two.txt',
        oldName: 'b.txt',
        newName: 'two.txt',
        currentName: 'two.txt',
        type: 'file',
        result: 'completed',
        message: null,
      },
    ],
    compensationAttempted: false,
    recoveryRequired: false,
    undoAvailable: true,
    undoExpiresAt: '2026-08-20T08:30:00Z',
    ...overrides,
  };
}
