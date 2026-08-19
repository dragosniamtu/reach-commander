import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SourceDto } from '../../../core/api/api.models';
import { SourceSelectorComponent } from './source-selector.component';

describe('SourceSelectorComponent', () => {
  let fixture: ComponentFixture<SourceSelectorComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [SourceSelectorComponent] }).compileComponents();
    fixture = TestBed.createComponent(SourceSelectorComponent);
    fixture.componentRef.setInput('sources', [source('downloads'), source('usb', false)]);
    fixture.componentRef.setInput('selectedSourceId', 'downloads');
    fixture.detectChanges();
  });

  it('renders compact source buttons and keeps unavailable sources visible', () => {
    const buttons = fixture.nativeElement.querySelectorAll('button');

    expect(buttons).toHaveLength(2);
    expect(buttons[0].getAttribute('aria-pressed')).toBe('true');
    expect(buttons[1].disabled).toBe(true);
    expect(buttons[1].textContent).toContain('USB');
  });

  it('emits an available selected source', () => {
    const selected = vi.fn();
    fixture.componentInstance.sourceSelected.subscribe(selected);

    fixture.nativeElement.querySelector('button').click();

    expect(selected).toHaveBeenCalledWith('downloads');
  });
});

function source(id: string, isAvailable = true): SourceDto {
  return {
    id,
    name: id === 'usb' ? 'USB' : 'Downloads',
    isAvailable,
    isReadOnly: id === 'usb',
    totalBytes: isAvailable ? 1000 : null,
    usedBytes: isAvailable ? 250 : null,
    freeBytes: isAvailable ? 750 : null,
    defaultLeft: id === 'downloads',
    defaultRight: false,
  };
}
