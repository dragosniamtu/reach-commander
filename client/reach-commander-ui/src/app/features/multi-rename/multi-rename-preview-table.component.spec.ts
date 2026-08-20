import { ComponentFixture, TestBed } from '@angular/core/testing';
import { BatchRenamePreviewRowDto } from '../../core/api/api.models';
import { MultiRenamePreviewTableComponent } from './multi-rename-preview-table.component';

describe('MultiRenamePreviewTableComponent', () => {
  let fixture: ComponentFixture<MultiRenamePreviewTableComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [MultiRenamePreviewTableComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(MultiRenamePreviewTableComponent);
  });

  it('shows old and complete new filenames plus row status', () => {
    fixture.componentRef.setInput('rows', [previewRow('Trip-001.jpg', 'ready')]);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('holiday-photo.jpg');
    expect(fixture.nativeElement.textContent).toContain('Trip-001.jpg');
    expect(fixture.nativeElement.textContent).toContain('Ready');
  });
});

function previewRow(
  newName: string,
  status: BatchRenamePreviewRowDto['status'],
): BatchRenamePreviewRowDto {
  return {
    sourcePath: '/Movies/holiday-photo.jpg',
    oldName: 'holiday-photo.jpg',
    oldExtension: 'jpg',
    newName,
    type: 'file',
    size: 10,
    modifiedAt: '2026-08-20T08:00:00Z',
    status,
    message: null,
  };
}
