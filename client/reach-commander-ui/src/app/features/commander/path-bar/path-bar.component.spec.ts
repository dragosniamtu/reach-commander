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
});
