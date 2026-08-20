import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { BatchRenamePreviewDto } from '../../core/api/api.models';
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
