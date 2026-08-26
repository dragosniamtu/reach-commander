import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CommandBarComponent } from './command-bar.component';

describe('CommandBarComponent', () => {
  let fixture: ComponentFixture<CommandBarComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [CommandBarComponent] }).compileComponents();
    fixture = TestBed.createComponent(CommandBarComponent);
    fixture.detectChanges();
  });

  it('keeps unavailable commands visible and enables the menu', () => {
    const rename: HTMLButtonElement = fixture.nativeElement.querySelector('[data-key="F4"]');
    const copy: HTMLButtonElement = fixture.nativeElement.querySelector('[data-key="F5"]');
    const menu: HTMLButtonElement = fixture.nativeElement.querySelector('[data-key="F9"]');

    expect(rename.disabled).toBe(true);
    expect(copy.disabled).toBe(true);
    expect(copy.getAttribute('aria-label')).toContain('unavailable');
    expect(menu.disabled).toBe(false);
  });

  it('enables F4 independently and exposes its exact disabled reason', () => {
    fixture.componentRef.setInput('availability', availability({
      rename: { enabled: true, reason: null },
    }));
    fixture.detectChanges();
    expect(button('F4').disabled).toBe(false);

    fixture.componentRef.setInput('availability', availability({
      rename: { enabled: false, reason: 'Symbolic links cannot be renamed.' },
    }));
    fixture.detectChanges();
    expect(button('F4').disabled).toBe(true);
    expect(button('F4').title).toBe('Symbolic links cannot be renamed.');
  });

  it('changes F5 to Extract only when the shell has an extraction context', () => {
    fixture.componentRef.setInput('availability', availability({
      copy: { enabled: true, reason: null, label: 'Extract' },
    }));
    fixture.detectChanges();
    const extract: HTMLButtonElement = fixture.nativeElement.querySelector('[data-key="F5"]');

    expect(extract.disabled).toBe(false);
    expect(extract.textContent).toContain('Extract');
  });

  it('enables F5 through F8 independently and exposes exact disabled reasons', () => {
    fixture.componentRef.setInput('availability', availability({
      copy: { enabled: true, reason: null, label: 'Copy' },
      move: { enabled: false, reason: 'The source is read-only.' },
      createDirectory: { enabled: true, reason: null },
      delete: { enabled: true, reason: null },
    }));
    fixture.detectChanges();

    expect(button('F5').disabled).toBe(false);
    expect(button('F6').disabled).toBe(true);
    expect(button('F6').title).toBe('The source is read-only.');
    expect(button('F7').disabled).toBe(false);
    expect(button('F8').disabled).toBe(false);
  });

  function button(key: string): HTMLButtonElement {
    return fixture.nativeElement.querySelector(`[data-key="${key}"]`);
  }
});

function availability(overrides: any = {}) {
  return {
    rename: { enabled: false, reason: 'Select or focus an item.' },
    copy: { enabled: false, reason: 'Select or focus an item.', label: 'Copy' },
    move: { enabled: false, reason: 'Select or focus an item.' },
    createDirectory: { enabled: false, reason: 'Choose a writable folder.' },
    delete: { enabled: false, reason: 'Select or focus an item.' },
    ...overrides,
  };
}
