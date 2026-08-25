import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SourceDto, TrashEntryDto } from '../../../core/api/api.models';
import { TrashStore } from './trash.store';
import { PERMANENT_DELETE_WARNING } from './delete-dialog.component';
import { TrashDialogComponent } from './trash-dialog.component';

describe('TrashDialogComponent', () => {
  let fixture: ComponentFixture<TrashDialogComponent>;
  const store = fakeStore();

  beforeEach(async () => {
    vi.clearAllMocks();
    store.sourceFilter.set(null);
    store.entries.set([entry('one', 'media'), entry('two', 'downloads')]);
    store.selection.set(new Set());
    store.restorePreview.set(null);
    store.restoreConflictDecisions.set(new Map());
    store.canSubmitRestore.set(false);
    store.busy.set(false);
    await TestBed.configureTestingModule({
      imports: [TrashDialogComponent],
      providers: [{ provide: TrashStore, useValue: store }],
    }).compileComponents();
    fixture = TestBed.createComponent(TrashDialogComponent);
    fixture.componentRef.setInput('sources', sources());
    fixture.detectChanges();
  });

  it('loads, filters, and toggles Trash entries with accessible selection', () => {
    expect(store.load).toHaveBeenCalledOnce();
    const filter = fixture.nativeElement.querySelector('#trash-source-filter') as HTMLSelectElement;
    filter.value = 'media';
    filter.dispatchEvent(new Event('change'));
    expect(store.setSourceFilter).toHaveBeenCalledWith('media');

    const checkbox = fixture.nativeElement.querySelector('[data-trash-id="one"]') as HTMLInputElement;
    checkbox.click();
    expect(store.toggleSelection).toHaveBeenCalledWith('one');
  });

  it('shows missing parents, resolves restore conflicts, and submits only when complete', () => {
    store.selection.set(new Set(['one']));
    store.restorePreview.set({
      planId: 'restore-plan', expiresAt: '2026-08-25T10:00:00Z', entries: [entry('one', 'media')],
      parentsToCreate: ['/Family'], conflicts: [{
        conflictId: 'conflict', sourceLogicalPath: '/one.txt', destinationLogicalPath: '/Family/one.txt',
        sourceType: 'file', destinationType: 'file', allowedDecisions: ['skip', 'createUniqueName'],
      }],
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('/Family');
    const select = fixture.nativeElement.querySelector('.restore-conflicts select') as HTMLSelectElement;
    select.value = 'createUniqueName';
    select.dispatchEvent(new Event('change'));
    expect(store.setRestoreConflictDecision).toHaveBeenCalledWith('conflict', 'createUniqueName');
    expect(button('Restore now').disabled).toBe(true);

    store.canSubmitRestore.set(true);
    fixture.detectChanges();
    button('Restore now').click();
    expect(store.submitRestore).toHaveBeenCalledOnce();
  });

  it('labels all-source and filtered Empty Trash scope and requires permanent confirmation', () => {
    button('Empty Trash for all sources').click();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain(PERMANENT_DELETE_WARNING);
    expect(button('Empty permanently').disabled).toBe(true);
    const checkbox = fixture.nativeElement.querySelector('#confirm-empty-trash') as HTMLInputElement;
    checkbox.click();
    fixture.detectChanges();
    button('Empty permanently').click();
    expect(store.emptyTrash).toHaveBeenCalledWith(true);

    store.sourceFilter.set('media');
    fixture.componentInstance.cancelEmptyConfirmation();
    fixture.detectChanges();
    expect(button('Empty Trash for Media')).not.toBeNull();
  });

  function button(label: string): HTMLButtonElement {
    return [...fixture.nativeElement.querySelectorAll('button')]
      .find((candidate: HTMLButtonElement) => candidate.textContent?.trim() === label)!;
  }
});

function fakeStore() {
  return {
    sourceFilter: signal<string | null>(null), entries: signal<readonly TrashEntryDto[]>([]),
    selection: signal<ReadonlySet<string>>(new Set()), restorePreview: signal<any>(null),
    restoreConflictDecisions: signal<ReadonlyMap<string, string>>(new Map()),
    deletePreview: signal(null), busy: signal(false), error: signal<string | null>(null),
    canSubmitRestore: signal(false), load: vi.fn(), setSourceFilter: vi.fn(),
    toggleSelection: vi.fn(), selectAll: vi.fn(), clearSelection: vi.fn(),
    previewSelectedRestore: vi.fn(), setRestoreConflictDecision: vi.fn(),
    submitRestore: vi.fn(), permanentlyDeleteSelected: vi.fn(), emptyTrash: vi.fn(),
  };
}

function entry(trashId: string, sourceId: string): TrashEntryDto {
  return {
    trashId, sourceId, originalLogicalPath: `/${trashId}.txt`, name: `${trashId}.txt`,
    type: 'file', size: 1, deletedAt: '2026-08-25T09:00:00Z',
  };
}

function sources(): readonly SourceDto[] {
  return [
    { id: 'media', name: 'Media', isAvailable: true, isReadOnly: false, totalBytes: 1, usedBytes: 0, freeBytes: 1, defaultLeft: true, defaultRight: false },
    { id: 'downloads', name: 'Downloads', isAvailable: true, isReadOnly: false, totalBytes: 1, usedBytes: 0, freeBytes: 1, defaultLeft: false, defaultRight: true },
  ];
}
