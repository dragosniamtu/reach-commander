import { ComponentFixture, TestBed } from '@angular/core/testing';
import { PanelState } from '../../../core/state/commander.models';
import { FileTableComponent } from './file-table.component';

describe('FileTableComponent', () => {
  let fixture: ComponentFixture<FileTableComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [FileTableComponent] }).compileComponents();
    fixture = TestBed.createComponent(FileTableComponent);
    fixture.componentRef.setInput('panel', panel());
    fixture.detectChanges();
  });

  it('renders dense sortable headers and selected row state', () => {
    const nameHeader = fixture.nativeElement.querySelector('[data-sort="name"]');
    const selected = fixture.nativeElement.querySelector('tbody tr[aria-selected="true"]');

    expect(nameHeader.closest('th')?.getAttribute('aria-sort')).toBe('ascending');
    expect(selected.textContent).toContain('movie.mkv');
  });

  it('emits pointer selection with modifier intent', () => {
    const selected = vi.fn();
    fixture.componentInstance.rowSelected.subscribe(selected);
    const row = fixture.nativeElement.querySelector('tbody tr');

    row.dispatchEvent(new MouseEvent('click', { bubbles: true, ctrlKey: true }));

    expect(selected).toHaveBeenCalledWith({ rowIndex: 0, mode: 'toggle' });
  });
});

function panel(): PanelState {
  return {
    sourceId: 'media',
    tabs: [{ id: 'tab', label: 'Movies', sourceId: 'media', path: '/' }],
    activeTabId: 'tab',
    cursorIndex: 0,
    selectedItems: new Set(['/movie.mkv']),
    selectionAnchor: 0,
    sortColumn: 'name',
    sortDirection: 'ascending',
    filter: '',
    entries: [{
      name: 'movie.mkv',
      relativePath: '/movie.mkv',
      type: 'file',
      size: 1024,
      modifiedAt: '2026-08-19T10:00:00Z',
      extension: 'mkv',
      isReadOnly: false,
      isSymbolicLink: false,
      attributes: 'Normal',
    }],
    loading: false,
    errorCode: null,
    requestToken: 1,
  };
}
