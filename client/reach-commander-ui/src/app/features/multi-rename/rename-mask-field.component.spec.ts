import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RenameMaskFieldComponent } from './rename-mask-field.component';

describe('RenameMaskFieldComponent', () => {
  let fixture: ComponentFixture<RenameMaskFieldComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [RenameMaskFieldComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(RenameMaskFieldComponent);
    fixture.componentRef.setInput('label', 'Name mask');
    fixture.componentRef.setInput('testId', 'name-mask');
    fixture.componentRef.setInput('value', 'File-');
    fixture.componentRef.setInput('tokens', [{ label: 'Counter', value: '[C]' }]);
    fixture.detectChanges();
  });

  it('inserts a token at the caret and restores input focus', () => {
    const emitted = vi.fn();
    fixture.componentInstance.valueChanged.subscribe(emitted);
    const input = fixture.nativeElement.querySelector('input') as HTMLInputElement;
    input.setSelectionRange(5, 5);

    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();

    expect(emitted).toHaveBeenCalledWith('File-[C]');
    expect(document.activeElement).toBe(input);
    expect(input.selectionStart).toBe(8);
  });
});
