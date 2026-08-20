import { ComponentFixture, TestBed } from '@angular/core/testing';
import { PathBarComponent } from './path-bar.component';

describe('PathBarComponent', () => {
  let fixture: ComponentFixture<PathBarComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [PathBarComponent] }).compileComponents();
    fixture = TestBed.createComponent(PathBarComponent);
    fixture.componentRef.setInput('path', '/Movies');
    fixture.detectChanges();
  });

  it('enters edit mode and commits a logical path', () => {
    const committed = vi.fn();
    fixture.componentInstance.pathCommitted.subscribe(committed);

    fixture.componentInstance.focusEditor();
    fixture.detectChanges();
    const input: HTMLInputElement = fixture.nativeElement.querySelector('input');
    input.value = '/Movies/Sci-Fi';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));

    expect(committed).toHaveBeenCalledWith('/Movies/Sci-Fi');
  });

  it('shows an archive path without allowing it to be edited', () => {
    fixture.componentRef.setInput('path', 'Downloads:/backups/photos.7z!/Family/2025');
    fixture.componentRef.setInput('readOnly', true);
    fixture.detectChanges();

    const display = fixture.nativeElement.querySelector('.path-display') as HTMLElement;
    expect((display as HTMLInputElement).value).toBe(
      'Downloads:/backups/photos.7z!/Family/2025',
    );
    expect(display.tagName).toBe('INPUT');
    expect(display.getAttribute('aria-readonly')).toBe('true');
    expect(display.tabIndex).toBe(0);

    fixture.componentInstance.focusEditor();
    fixture.detectChanges();
    const readOnlyInput = fixture.nativeElement.querySelector('input') as HTMLInputElement;
    expect(readOnlyInput.readOnly).toBe(true);
    expect(document.activeElement).toBe(readOnlyInput);
    expect(readOnlyInput.selectionStart).toBe(0);
    expect(readOnlyInput.selectionEnd).toBe(readOnlyInput.value.length);
  });
});
