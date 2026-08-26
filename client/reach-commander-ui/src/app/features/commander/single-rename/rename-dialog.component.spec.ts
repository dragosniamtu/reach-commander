import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { BatchRenamePreviewDto, BatchRenamePreviewStatus } from '../../../core/api/api.models';
import { SingleRenameState } from '../../../core/state/single-rename.models';
import { SingleRenameStore } from '../../../core/state/single-rename-store';
import { RenameDialogComponent } from './rename-dialog.component';

describe('RenameDialogComponent', () => {
  let fixture: ComponentFixture<RenameDialogComponent>;
  const fakeStore = {
    state: signal<SingleRenameState>(openState()),
    canExecute: signal(false),
    setName: vi.fn(),
    execute: vi.fn(() => Promise.resolve(true)),
  };

  beforeEach(async () => {
    vi.clearAllMocks();
    fakeStore.state.set(openState());
    fakeStore.canExecute.set(false);
    await TestBed.configureTestingModule({
      imports: [RenameDialogComponent],
      providers: [{ provide: SingleRenameStore, useValue: fakeStore }],
    }).compileComponents();
    fixture = TestBed.createComponent(RenameDialogComponent);
  });

  it('labels the entry type and selects the complete current name', () => {
    fixture.detectChanges();
    const dialog = fixture.nativeElement.querySelector('[role="dialog"]') as HTMLElement;
    const input = fixture.nativeElement.querySelector(
      '#single-rename-name',
    ) as HTMLInputElement;

    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(fixture.nativeElement.textContent).toContain('Rename file');
    expect(input.value).toBe('holiday.txt');
    expect(document.activeElement).toBe(input);
    expect(input.selectionStart).toBe(0);
    expect(input.selectionEnd).toBe('holiday.txt'.length);
  });

  it('labels directories as folders', () => {
    fakeStore.state.set(
      openState({
        context: {
          ...openState().context!,
          entry: { ...openState().context!.entry, name: 'Drafts', type: 'directory' },
        },
        newName: 'Drafts',
      }),
    );
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Rename folder');
  });

  it('keeps a conflict visible and Rename disabled', () => {
    fakeStore.state.set(
      openState({
        newName: 'taken.txt',
        preview: previewResponse(
          'taken.txt',
          'conflict',
          'The destination name is already in use.',
        ),
      }),
    );
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('already in use');
    expect(button('single-rename-submit').disabled).toBe(true);
  });

  it('maps input and Enter to the store', () => {
    fakeStore.canExecute.set(true);
    fixture.detectChanges();
    const input = fixture.nativeElement.querySelector(
      '#single-rename-name',
    ) as HTMLInputElement;
    input.value = '[N]-literal.txt';
    input.dispatchEvent(new Event('input'));
    fixture.componentInstance.handleKeydown(
      new KeyboardEvent('keydown', { key: 'Enter' }),
    );

    expect(fakeStore.setName).toHaveBeenCalledWith('[N]-literal.txt');
    expect(fakeStore.execute).toHaveBeenCalledOnce();
  });

  it('maps Escape to close only when execution is not pending', () => {
    const closed = vi.fn();
    fixture.componentInstance.closeRequested.subscribe(closed);
    fixture.detectChanges();

    fixture.componentInstance.handleKeydown(
      new KeyboardEvent('keydown', { key: 'Escape' }),
    );
    expect(closed).toHaveBeenCalledOnce();

    fakeStore.state.update((state) => ({ ...state, actionPending: true }));
    fixture.componentInstance.handleKeydown(
      new KeyboardEvent('keydown', { key: 'Escape' }),
    );
    expect(closed).toHaveBeenCalledOnce();
  });

  function button(testId: string): HTMLButtonElement {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }
});

function openState(overrides: Partial<SingleRenameState> = {}): SingleRenameState {
  return {
    open: true,
    context: {
      panelSide: 'left',
      sourceId: 'media',
      sourceName: 'Media',
      directoryPath: '/Movies',
      entry: {
        name: 'holiday.txt',
        relativePath: '/Movies/holiday.txt',
        type: 'file',
        size: 7,
        modifiedAt: '2026-08-26T07:00:00Z',
        extension: 'txt',
        isReadOnly: false,
        isSymbolicLink: false,
        attributes: 'Normal',
        archiveFormatHint: null,
        archiveRole: null,
      },
      isAvailable: true,
      isReadOnly: false,
    },
    newName: 'holiday.txt',
    preview: null,
    operation: null,
    previewPending: false,
    actionPending: false,
    errorCode: null,
    requestToken: 1,
    ...overrides,
  };
}

function previewResponse(
  newName: string,
  status: BatchRenamePreviewStatus,
  message: string | null,
): BatchRenamePreviewDto {
  return {
    planId: '11111111-1111-4111-8111-111111111111',
    expiresAt: '2026-08-26T08:10:00Z',
    rows: [
      {
        sourcePath: '/Movies/holiday.txt',
        oldName: 'holiday.txt',
        oldExtension: 'txt',
        newName,
        type: 'file',
        size: 7,
        modifiedAt: '2026-08-26T07:00:00Z',
        status,
        message,
      },
    ],
    canExecute: status === 'ready',
    changedCount: status === 'ready' ? 1 : 0,
    unchangedCount: status === 'unchanged' ? 1 : 0,
    invalidCount: status === 'ready' || status === 'unchanged' ? 0 : 1,
  };
}
