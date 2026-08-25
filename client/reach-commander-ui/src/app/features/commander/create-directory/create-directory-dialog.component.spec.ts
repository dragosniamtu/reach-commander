import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CommanderApiPort, FileEntryDto } from '../../../core/api/api.models';
import { CommanderApiTestBase } from '../../../testing/commander-api-test-base';
import { CreateDirectoryDialogComponent, validateDirectoryName } from './create-directory-dialog.component';

describe('CreateDirectoryDialogComponent', () => {
  let fixture: ComponentFixture<CreateDirectoryDialogComponent>;
  let api: FakeDirectoryApi;

  beforeEach(async () => {
    api = new FakeDirectoryApi();
    await TestBed.configureTestingModule({
      imports: [CreateDirectoryDialogComponent],
      providers: [{ provide: CommanderApiPort, useValue: api }],
    }).compileComponents();
    fixture = TestBed.createComponent(CreateDirectoryDialogComponent);
    fixture.componentRef.setInput('sourceId', 'media');
    fixture.componentRef.setInput('sourceName', 'Media');
    fixture.componentRef.setInput('parentLogicalPath', '/Movies');
    fixture.detectChanges();
  });

  it('creates exactly one directory on Enter and emits completion', async () => {
    const created = vi.fn();
    fixture.componentInstance.created.subscribe(created);
    const input = fixture.nativeElement.querySelector('#directory-name') as HTMLInputElement;
    input.value = 'Family';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await fixture.whenStable();

    expect(api.requests).toEqual([{
      sourceId: 'media', parentLogicalPath: '/Movies', name: 'Family',
    }]);
    expect(created).toHaveBeenCalledOnce();
  });

  it.each([
    ['', 'Enter a directory name.'], ['.', "A directory cannot be named '.' or '..'."],
    ['../escape', 'Directory names cannot contain path separators.'],
    ['CON.txt', 'This name is reserved by Windows.'],
    ['.reachcommander-trash', 'This name is reserved by ReachCommander.'],
    ['name.', 'A directory name cannot end with a dot or space.'],
  ])('rejects %s before reaching the API', (name, message) => {
    expect(validateDirectoryName(name)).toBe(message);
  });

  it('keeps Escape blocked while busy and exposes safe API errors', async () => {
    const closed = vi.fn();
    fixture.componentInstance.closeRequested.subscribe(closed);
    api.error = { error: { detail: 'A directory with that name already exists.' } };
    fixture.componentInstance.setName('Family');
    await fixture.componentInstance.submit();
    expect(fixture.componentInstance.error()).toContain('already exists');

    fixture.componentInstance.busy.set(true);
    fixture.componentInstance.handleKeydown(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(closed).not.toHaveBeenCalled();
  });
});

class FakeDirectoryApi extends CommanderApiTestBase {
  readonly requests: any[] = [];
  error: unknown = null;
  override createDirectory(request: any): Promise<FileEntryDto> {
    this.requests.push(request);
    if (this.error) return Promise.reject(this.error);
    return Promise.resolve({
      name: request.name, relativePath: `${request.parentLogicalPath}/${request.name}`,
      type: 'directory', size: null, modifiedAt: null, extension: null, isReadOnly: false,
      isSymbolicLink: false, attributes: '', archiveFormatHint: null, archiveRole: null,
    });
  }
  override getSystemMetrics(): any { throw new Error('unused'); }
  override getSources(): any { throw new Error('unused'); }
  override listFiles(): any { throw new Error('unused'); }
  override listArchive(): any { throw new Error('unused'); }
  override getInfo(): any { throw new Error('unused'); }
  override getUploadLimits(): any { throw new Error('unused'); }
  override uploadFiles(): any { throw new Error('unused'); }
  override previewBatchRename(): any { throw new Error('unused'); }
  override executeBatchRename(): any { throw new Error('unused'); }
  override undoBatchRename(): any { throw new Error('unused'); }
  override previewArchiveExtraction(): any { throw new Error('unused'); }
  override executeArchiveExtraction(): any { throw new Error('unused'); }
  override getArchiveExtraction(): any { throw new Error('unused'); }
  override cancelArchiveExtraction(): any { throw new Error('unused'); }
}
