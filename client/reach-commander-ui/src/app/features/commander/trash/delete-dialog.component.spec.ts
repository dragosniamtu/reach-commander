import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { DeletePreviewDto, DeletePreviewRequestDto } from '../../../core/api/api.models';
import { TrashStore } from './trash.store';
import { DeleteDialogComponent, PERMANENT_DELETE_WARNING } from './delete-dialog.component';

describe('DeleteDialogComponent', () => {
  let fixture: ComponentFixture<DeleteDialogComponent>;
  const store = fakeStore();

  beforeEach(async () => {
    vi.clearAllMocks();
    store.deleteRequest.set(request());
    store.deletePreview.set(preview());
    store.busy.set(false);
    store.error.set(null);
    await TestBed.configureTestingModule({
      imports: [DeleteDialogComponent],
      providers: [{ provide: TrashStore, useValue: store }],
    }).compileComponents();
    fixture = TestBed.createComponent(DeleteDialogComponent);
    fixture.detectChanges();
  });

  it('defaults to recoverable Trash and shows a bounded captured-name summary', () => {
    expect(fixture.nativeElement.textContent).toContain('Move to Trash');
    expect(fixture.nativeElement.textContent).not.toContain(PERMANENT_DELETE_WARNING);
    expect(fixture.nativeElement.textContent).toContain('one.txt');
    expect(fixture.nativeElement.textContent).toContain('and 2 more');

    button('Move to Trash').click();
    expect(store.submitDelete).toHaveBeenCalledWith(false);
  });

  it('requires the Permanent delete checkbox and exact irreversible warning', () => {
    const checkbox = fixture.nativeElement.querySelector('#permanent-delete') as HTMLInputElement;
    checkbox.checked = true;
    checkbox.dispatchEvent(new Event('change'));
    expect(store.changeDeleteMode).toHaveBeenCalledWith('permanent');

    store.deletePreview.set(preview({ mode: 'permanent' }));
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain(PERMANENT_DELETE_WARNING);
    expect(button('Delete forever').disabled).toBe(false);
    button('Delete forever').click();
    expect(store.submitDelete).toHaveBeenCalledWith(true);
  });

  it('forces permanent mode and explains when Trash is unavailable', () => {
    store.deletePreview.set(preview({
      trashAvailable: false,
      trashUnavailableReason: 'Managed Trash is unavailable on this source.',
    }));
    fixture.detectChanges();
    fixture.componentInstance.ensureAvailableMode();

    expect(store.changeDeleteMode).toHaveBeenCalledWith('permanent');
    expect(fixture.nativeElement.textContent).toContain('Managed Trash is unavailable');
  });

  function button(label: string): HTMLButtonElement {
    return [...fixture.nativeElement.querySelectorAll('button')]
      .find((candidate: HTMLButtonElement) => candidate.textContent?.trim() === label)!;
  }
});

function fakeStore() {
  return {
    deleteRequest: signal<DeletePreviewRequestDto | null>(request()),
    deletePreview: signal<DeletePreviewDto | null>(preview()),
    busy: signal(false),
    error: signal<string | null>(null),
    changeDeleteMode: vi.fn(),
    submitDelete: vi.fn(),
    clearDeletePreview: vi.fn(),
  };
}

function request(): DeletePreviewRequestDto {
  return {
    sourceId: 'media',
    logicalPaths: ['/one.txt', '/two.txt', '/three.txt', '/four.txt', '/five.txt', '/six.txt', '/seven.txt'],
    mode: 'trash',
  };
}

function preview(overrides: Partial<DeletePreviewDto> = {}): DeletePreviewDto {
  return {
    planId: 'delete-plan', expiresAt: '2026-08-25T10:00:00Z', mode: 'trash',
    trashAvailable: true, trashUnavailableReason: null, totalItems: 7, totalBytes: 7,
    ...overrides,
  };
}
