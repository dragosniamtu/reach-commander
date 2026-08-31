import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  SourceAddRequestDto,
  SourceDto,
  SourceManagementOperationDto,
} from '../../core/api/api.models';
import { SourceManagementStore } from '../../core/state/source-management.store';
import {
  SourceManagementDialogComponent,
  validateSourceDisplayName,
  validateUbuntuHostPath,
} from './source-management-dialog.component';

describe('SourceManagementDialogComponent', () => {
  let fixture: ComponentFixture<SourceManagementDialogComponent>;
  let store: FakeSourceManagementStore;
  let opener: HTMLButtonElement;

  beforeEach(async () => {
    store = new FakeSourceManagementStore();
    await TestBed.configureTestingModule({
      imports: [SourceManagementDialogComponent],
      providers: [{ provide: SourceManagementStore, useValue: store }],
    }).compileComponents();
    opener = document.createElement('button');
    document.body.append(opener);
    fixture = TestBed.createComponent(SourceManagementDialogComponent);
    fixture.componentRef.setInput('opener', opener);
    fixture.detectChanges();
  });

  afterEach(() => opener.remove());

  it('traps focus, labels the modal, and restores its opener after Escape', async () => {
    const closed = vi.fn();
    fixture.componentInstance.closed.subscribe(closed);
    await fixture.whenStable();

    const dialog = fixture.nativeElement.querySelector('dialog') as HTMLDialogElement;
    const name = fixture.nativeElement.querySelector('#source-display-name') as HTMLInputElement;
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(dialog.getAttribute('aria-labelledby')).toBe('source-management-title');
    expect(document.activeElement).toBe(name);

    fixture.componentInstance.handleKeydown(new KeyboardEvent('keydown', { key: 'Escape' }));

    expect(closed).toHaveBeenCalledOnce();
    expect(document.activeElement).toBe(opener);
  });

  it.each([
    ['', 'Enter a display name.'],
    ['   ', 'Enter a display name.'],
    ['bad\u0000name', 'control characters'],
    ['x'.repeat(81), '80 characters'],
  ])('rejects display name %j before submission', (name, message) => {
    expect(validateSourceDisplayName(name)).toContain(message);
  });

  it.each([
    ['', 'absolute Ubuntu path'],
    ['srv/media', 'absolute Ubuntu path'],
    ['C:\\Media', 'absolute Ubuntu path'],
    ['/', 'specific folder'],
    ['/srv', 'specific folder'],
    ['/home', 'specific folder'],
    ['/mnt', 'specific folder'],
    ['/proc/1/root', 'protected system folder'],
    ['/sys/class', 'protected system folder'],
    ['/dev/disk', 'protected system folder'],
    ['/run/secrets', 'protected system folder'],
    ['/var/run/docker.sock', 'protected system folder'],
    ['/srv/media\\family', 'backslashes'],
    [`/srv/${'x'.repeat(1020)}`, '1,024 characters'],
  ])('rejects host path %j before submission', (path, message) => {
    expect(validateUbuntuHostPath(path)).toContain(message);
  });

  it('defaults to read-only and submits one trimmed narrow request', async () => {
    fixture.componentInstance.setDisplayName('  Family media  ');
    fixture.componentInstance.setHostPath('/srv/media/family');

    await fixture.componentInstance.submit();

    expect(store.submit).toHaveBeenCalledWith({
      displayName: 'Family media',
      hostPath: '/srv/media/family',
      access: 'readOnly',
    });
  });

  it('requires an explicit acknowledgement before a read/write mapping', async () => {
    fixture.componentInstance.setDisplayName('Family media');
    fixture.componentInstance.setHostPath('/srv/media/family');
    fixture.componentInstance.setAccess('readWrite');
    fixture.detectChanges();

    const warning = fixture.nativeElement.querySelector('[data-testid="read-write-warning"]');
    expect(warning.textContent).toContain('change or delete files in this host folder');
    expect(fixture.componentInstance.canSubmit()).toBe(false);
    await fixture.componentInstance.submit();
    expect(store.submit).not.toHaveBeenCalled();

    fixture.componentInstance.setReadWriteConfirmed(true);
    await fixture.componentInstance.submit();
    expect(store.submit).toHaveBeenCalledWith(expect.objectContaining({ access: 'readWrite' }));
  });

  it('confirms mapping-only removal and preserves host files in the message', async () => {
    store.mode.set('remove');
    store.removalSource.set(source('archive', 'Archive'));
    fixture.detectChanges();

    const dialog = fixture.nativeElement.querySelector('dialog') as HTMLDialogElement;
    expect(dialog.textContent).toContain('Remove source mapping?');
    expect(dialog.textContent).toContain('Remove Archive from ReachCommander?');
    expect(dialog.textContent).toContain('every file inside it will remain untouched');

    await fixture.componentInstance.submit();

    expect(store.submitRemoval).toHaveBeenCalledOnce();
    expect(store.submit).not.toHaveBeenCalled();
  });

  it('disables every duplicate submit path and Escape while an operation is active', async () => {
    fixture.componentInstance.setDisplayName('Family media');
    fixture.componentInstance.setHostPath('/srv/media/family');
    store.pending.set(true);
    fixture.detectChanges();
    const closed = vi.fn();
    fixture.componentInstance.closed.subscribe(closed);

    await fixture.componentInstance.submit();
    fixture.componentInstance.handleKeydown(new KeyboardEvent('keydown', { key: 'Enter' }));
    fixture.componentInstance.handleKeydown(new KeyboardEvent('keydown', { key: 'Escape' }));

    expect(store.submit).not.toHaveBeenCalled();
    expect(closed).not.toHaveBeenCalled();
    expect((fixture.nativeElement.querySelector('[data-testid="add-source-submit"]') as HTMLButtonElement).disabled).toBe(true);
  });

  it('submits Enter from text fields without hijacking Cancel, radios, or the RW checkbox', async () => {
    fixture.componentInstance.setDisplayName('Family media');
    fixture.componentInstance.setHostPath('/srv/media/family');
    fixture.detectChanges();
    const name = fixture.nativeElement.querySelector('#source-display-name') as HTMLInputElement;
    name.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await fixture.whenStable();
    expect(store.submit).toHaveBeenCalledOnce();

    store.submit.mockClear();
    const cancel = [...fixture.nativeElement.querySelectorAll('button')]
      .find((candidate: HTMLButtonElement) => candidate.textContent?.includes('Cancel'))!;
    cancel.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    const readOnly = fixture.nativeElement.querySelector(
      'input[type="radio"][value="readOnly"]',
    ) as HTMLInputElement;
    readOnly.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    fixture.componentInstance.setAccess('readWrite');
    fixture.detectChanges();
    const confirmation = fixture.nativeElement.querySelector(
      '[data-testid="read-write-warning"] input',
    ) as HTMLInputElement;
    confirmation.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await fixture.whenStable();

    expect(store.submit).not.toHaveBeenCalled();
  });

  it('moves focus to a stable in-dialog target across operation and terminal transitions', async () => {
    store.pending.set(true);
    store.operation.set(operation({ phase: 'accepted' }));
    fixture.detectChanges();
    await fixture.whenStable();
    const target = fixture.nativeElement.querySelector(
      '[data-testid="source-operation-focus"]',
    ) as HTMLElement;
    expect(document.activeElement).toBe(target);

    store.pending.set(false);
    store.operation.set(operation({ phase: 'failed' }));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(document.activeElement).toBe(target);
  });

  it('keeps Tab traversal trapped away from the background toolbar opener', async () => {
    store.pending.set(true);
    store.operation.set(operation({ phase: 'restarting' }));
    fixture.detectChanges();
    await fixture.whenStable();
    const anchors = fixture.nativeElement.querySelectorAll('.cdk-focus-trap-anchor');
    (anchors.item(anchors.length - 1) as HTMLElement).focus();
    await fixture.whenStable();

    expect(document.activeElement).not.toBe(opener);
    expect(fixture.nativeElement.contains(document.activeElement)).toBe(true);
  });

  it('does not announce catalog success until the generated source is in the fresh catalog', () => {
    store.operation.set(operation({ phase: 'completed', sourceId: 'family-media' }));
    store.catalogRefreshed.set(false);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Refreshing source list');
    expect(fixture.nativeElement.textContent).not.toContain('available in both panes');
    expect(fixture.nativeElement.querySelector('.operation-icon.success')).toBeNull();
  });

  it('shows only bounded public store errors and terminal rollback guidance', () => {
    store.error.set({
      code: 'source_management_validation_failed',
      detail: 'Choose a more specific existing host folder.',
    });
    store.operation.set(operation({
      phase: 'rolledBack',
      reasonCode: 'source_rolled_back',
      detail: 'The previous source configuration was restored.',
    }));
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Choose a more specific existing host folder.');
    expect(text).toContain('previous source configuration was restored');
    expect(text).not.toContain('/opt/reachcommander');
  });

  it('shows trusted-parent prerequisites and static host diagnostics for a real failed operation', () => {
    const initialText = fixture.nativeElement.textContent as string;
    expect(initialText).toContain('root-owned');
    expect(initialText).toContain('not group- or world-writable');
    expect(initialText).toContain('source folder itself may be owned by the runtime UID/GID');
    expect(initialText).toContain('/home/user/…');
    expect(initialText).toContain('root-controlled stable mount');

    store.operation.set(operation({
      phase: 'failed',
      reasonCode: 'untrusted_source_ancestry',
      detail: "The source folder's parent directories must be root-owned and not group- or world-writable.",
    }));
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('sudo reachcommander doctor');
    expect(text).toContain('support diagnostics');
    expect(text).not.toContain('/home/private');
  });

  it('presents reconnect and completed catalog-refresh states', () => {
    store.pending.set(true);
    store.reconnecting.set(true);
    store.operation.set(operation({ phase: 'restarting' }));
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Reconnecting to ReachCommander');

    store.pending.set(false);
    store.reconnecting.set(false);
    store.catalogRefreshed.set(true);
    store.operation.set(operation({ phase: 'completed', sourceId: 'family-media' }));
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('available in both panes');
  });
});

class FakeSourceManagementStore {
  readonly mode = signal<'add' | 'remove'>('add');
  readonly removalSource = signal<SourceDto | null>(null);
  readonly pending = signal(false);
  readonly reconnecting = signal(false);
  readonly operation = signal<SourceManagementOperationDto | null>(null);
  readonly error = signal<{ code: string; detail: string } | null>(null);
  readonly catalogRefreshed = signal(false);
  readonly terminal = signal(false);
  readonly submit = vi.fn((_request: SourceAddRequestDto) => Promise.resolve());
  readonly submitRemoval = vi.fn(() => Promise.resolve());
  readonly close = vi.fn();
}

function source(id: string, name: string): SourceDto {
  return {
    id,
    name,
    isAvailable: true,
    isReadOnly: false,
    totalBytes: 100,
    usedBytes: 25,
    freeBytes: 75,
    defaultLeft: false,
    defaultRight: false,
  };
}

function operation(
  overrides: Partial<SourceManagementOperationDto> = {},
): SourceManagementOperationDto {
  return {
    operationId: '33333333-3333-4333-8333-333333333333',
    sourceId: null,
    displayName: 'Family media',
    phase: 'accepted',
    reasonCode: 'accepted',
    detail: 'The source-management operation was accepted.',
    createdAt: '2026-08-31T08:00:00Z',
    updatedAt: '2026-08-31T08:00:00Z',
    ...overrides,
  };
}
