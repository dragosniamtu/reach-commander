import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NameDiffComponent } from './name-diff.component';

describe('NameDiffComponent', () => {
  let fixture: ComponentFixture<NameDiffComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [NameDiffComponent] }).compileComponents();
    fixture = TestBed.createComponent(NameDiffComponent);
  });

  it('renders the complete proposed filename with an accessible label', () => {
    fixture.componentRef.setInput('oldName', 'holiday-photo.jpg');
    fixture.componentRef.setInput('newName', 'Trip-001.jpg');
    fixture.detectChanges();

    const output: HTMLElement = fixture.nativeElement.querySelector('[data-testid="new-name"]');
    expect(output.textContent).toContain('Trip-001.jpg');
    expect(output.getAttribute('aria-label')).toBe('New filename: Trip-001.jpg');
    expect(output.querySelector('mark')?.textContent).toContain('Trip-001');
  });

  it('renders an unchanged complete name without a change mark', () => {
    fixture.componentRef.setInput('oldName', 'same.txt');
    fixture.componentRef.setInput('newName', 'same.txt');
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('same.txt');
    expect(fixture.nativeElement.querySelector('mark')).toBeNull();
  });
});
