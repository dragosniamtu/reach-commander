import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SourceDto } from '../../../core/api/api.models';
import { SourceSelectorComponent } from './source-selector.component';

describe('SourceSelectorComponent', () => {
  let fixture: ComponentFixture<SourceSelectorComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [SourceSelectorComponent] }).compileComponents();
    fixture = TestBed.createComponent(SourceSelectorComponent);
    fixture.componentRef.setInput('sources', [
      source('downloads'),
      source('archive', true, true),
      source('usb', false, false),
    ]);
    fixture.componentRef.setInput('selectedSourceId', 'downloads');
    fixture.componentRef.setInput('removalEnabled', true);
    fixture.detectChanges();
  });

  it('renders compact source buttons and keeps unavailable sources visible', () => {
    const buttons = fixture.nativeElement.querySelectorAll('.source-button');

    expect(buttons).toHaveLength(3);
    expect(buttons[0].getAttribute('aria-pressed')).toBe('true');
    expect(buttons[2].disabled).toBe(true);
    expect(buttons[2].textContent).toContain('USB');
  });

  it('emits a dedicated remove request without selecting the source', () => {
    const removed = vi.fn();
    const selected = vi.fn();
    fixture.componentInstance.sourceRemovalRequested.subscribe(removed);
    fixture.componentInstance.sourceSelected.subscribe(selected);

    const removeButton = fixture.nativeElement.querySelector(
      '[data-testid="remove-source-archive"]',
    ) as HTMLButtonElement;
    removeButton.click();

    expect(removed).toHaveBeenCalledWith(expect.objectContaining({
      source: expect.objectContaining({ id: 'archive', name: 'Archive' }),
      opener: removeButton,
    }));
    expect(selected).not.toHaveBeenCalled();
  });

  it('keeps the final source remove control visible but disabled', () => {
    fixture.componentRef.setInput('sources', [source('downloads')]);
    fixture.detectChanges();

    const removeButton = fixture.nativeElement.querySelector(
      '[data-testid="remove-source-downloads"]',
    ) as HTMLButtonElement;
    expect(removeButton.disabled).toBe(true);
    expect(removeButton.title).toContain('at least one source');
  });

  it('shows exactly one accessible source access policy token', () => {
    const writable = fixture.nativeElement.querySelector('[data-testid="source-downloads"]');
    const readOnly = fixture.nativeElement.querySelector('[data-testid="source-archive"]');
    const unavailable = fixture.nativeElement.querySelector('[data-testid="source-usb"]');

    expect(writable.querySelector('[data-access="writable"]')?.textContent).toContain('RW');
    expect(readOnly.querySelector('[data-access="read-only"]')?.textContent).toContain('RO');
    expect(writable.querySelectorAll('[data-access]')).toHaveLength(1);
    expect(readOnly.querySelectorAll('[data-access]')).toHaveLength(1);
    expect(unavailable.getAttribute('aria-label')).toContain('unavailable');
    expect(unavailable.getAttribute('aria-label')).toContain('read/write');
    expect(unavailable.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('emits an available selected source', () => {
    const selected = vi.fn();
    fixture.componentInstance.sourceSelected.subscribe(selected);

    fixture.nativeElement.querySelector('button').click();

    expect(selected).toHaveBeenCalledWith('downloads');
  });
});

function source(id: string, isAvailable = true, isReadOnly = false): SourceDto {
  return {
    id,
    name: id === 'usb' ? 'USB' : id[0]!.toUpperCase() + id.slice(1),
    isAvailable,
    isReadOnly,
    totalBytes: isAvailable ? 1000 : null,
    usedBytes: isAvailable ? 250 : null,
    freeBytes: isAvailable ? 750 : null,
    defaultLeft: id === 'downloads',
    defaultRight: false,
  };
}
