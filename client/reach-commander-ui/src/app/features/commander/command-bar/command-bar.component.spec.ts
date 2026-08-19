import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CommandBarComponent } from './command-bar.component';

describe('CommandBarComponent', () => {
  let fixture: ComponentFixture<CommandBarComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [CommandBarComponent] }).compileComponents();
    fixture = TestBed.createComponent(CommandBarComponent);
    fixture.detectChanges();
  });

  it('keeps future mutation commands visible but disabled and enables the menu', () => {
    const copy: HTMLButtonElement = fixture.nativeElement.querySelector('[data-key="F5"]');
    const menu: HTMLButtonElement = fixture.nativeElement.querySelector('[data-key="F9"]');

    expect(copy.disabled).toBe(true);
    expect(copy.getAttribute('aria-describedby')).toBeTruthy();
    expect(menu.disabled).toBe(false);
  });
});
