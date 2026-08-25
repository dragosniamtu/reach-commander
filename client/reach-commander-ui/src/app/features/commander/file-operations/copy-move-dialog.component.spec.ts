import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CapturedFileOperationContext } from '../../../core/state/file-operation.models';
import { FileOperationStore } from './file-operation.store';
import { CopyMoveDialogComponent } from './copy-move-dialog.component';

describe('CopyMoveDialogComponent', () => {
  let fixture: ComponentFixture<CopyMoveDialogComponent>;
  const store = fakeStore();

  beforeEach(async () => {
    vi.clearAllMocks();
    store.context.set(context());
    store.destination.set('/Movies');
    store.preview.set(preview());
    store.busy.set(false);
    store.error.set(null);
    store.canSubmit.set(false);
    await TestBed.configureTestingModule({
      imports: [CopyMoveDialogComponent],
      providers: [{ provide: FileOperationStore, useValue: store }],
    }).compileComponents();
    fixture = TestBed.createComponent(CopyMoveDialogComponent);
    fixture.detectChanges();
  });

  it('shows the immutable route and normalizes destination changes through the store', () => {
    expect(fixture.nativeElement.textContent).toContain('2 items');
    expect(fixture.nativeElement.textContent).toContain('alpha.txt');
    expect(fixture.nativeElement.textContent).toContain('beta.txt');
    expect(fixture.nativeElement.textContent).toContain('Downloads');
    expect(fixture.nativeElement.textContent).toContain('Media');

    const input = fixture.nativeElement.querySelector('#operation-destination') as HTMLInputElement;
    input.value = '\\New\\Folder';
    input.dispatchEvent(new Event('input'));
    expect(store.setDestination).toHaveBeenCalledWith('\\New\\Folder');
  });

  it('applies a selected conflict action to remaining conflicts and gates Start', () => {
    const selects = fixture.nativeElement.querySelectorAll('select');
    selects[0].value = 'createUniqueName';
    selects[0].dispatchEvent(new Event('change'));
    const checkbox = fixture.nativeElement.querySelector('#apply-remaining') as HTMLInputElement;
    checkbox.checked = true;
    checkbox.dispatchEvent(new Event('change'));

    expect(store.setConflictDecision).toHaveBeenCalledWith('one', 'createUniqueName');
    expect(store.applyDecisionToRemaining).toHaveBeenCalledWith('createUniqueName');
    expect(button('Start').disabled).toBe(true);

    store.canSubmit.set(true);
    fixture.detectChanges();
    expect(button('Start').disabled).toBe(false);
    button('Start').click();
    expect(store.submit).toHaveBeenCalledOnce();
  });

  it('traps modal keystrokes and closes confirmation on Escape or Cancel', () => {
    const keydown = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true });
    fixture.nativeElement.querySelector('dialog').dispatchEvent(keydown);
    expect(store.closeConfirmation).toHaveBeenCalledOnce();

    button('Cancel').click();
    expect(store.closeConfirmation).toHaveBeenCalledTimes(2);
    expect(fixture.nativeElement.querySelector('dialog').getAttribute('aria-modal')).toBe('true');
  });

  function button(label: string): HTMLButtonElement {
    return [...fixture.nativeElement.querySelectorAll('button')]
      .find((candidate: HTMLButtonElement) => candidate.textContent?.trim() === label)!;
  }
});

function fakeStore() {
  return {
    context: signal<CapturedFileOperationContext | null>(context()),
    destination: signal('/Movies'),
    preview: signal<any>(preview()),
    conflictDecisions: signal<ReadonlyMap<string, string>>(new Map()),
    busy: signal(false),
    error: signal<string | null>(null),
    canSubmit: signal(false),
    setDestination: vi.fn(),
    setConflictDecision: vi.fn(),
    applyDecisionToRemaining: vi.fn(),
    submit: vi.fn(),
    closeConfirmation: vi.fn(),
  };
}

function context(): CapturedFileOperationContext {
  return {
    kind: 'copy', sourceId: 'Downloads', logicalPaths: ['/alpha.txt', '/beta.txt'],
    destinationSourceId: 'Media', destinationLogicalDirectory: '/Movies',
    selectedNames: ['alpha.txt', 'beta.txt'], knownTotalBytes: 2,
  };
}

function preview() {
  return {
    kind: 'copy', sourceId: 'Downloads', logicalPaths: ['/alpha.txt', '/beta.txt'],
    destinationSourceId: 'Media', destinationLogicalDirectory: '/Movies',
    planId: 'plan', expiresAt: '2026-08-25T10:00:00Z', totalItems: 2, totalBytes: 2,
    warnings: [], conflicts: [
      { conflictId: 'one', sourceLogicalPath: '/alpha.txt', destinationLogicalPath: '/Movies/alpha.txt', sourceType: 'file', destinationType: 'file', allowedDecisions: ['overwrite', 'skip', 'createUniqueName'] },
      { conflictId: 'two', sourceLogicalPath: '/beta.txt', destinationLogicalPath: '/Movies/beta.txt', sourceType: 'file', destinationType: 'file', allowedDecisions: ['skip', 'createUniqueName'] },
    ],
  };
}
