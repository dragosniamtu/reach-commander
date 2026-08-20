import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  ViewChild,
  input,
  output,
} from '@angular/core';

export interface RenameMaskToken {
  readonly label: string;
  readonly value: string;
}

@Component({
  selector: 'app-rename-mask-field',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './rename-mask-field.component.html',
  styleUrl: './rename-mask-field.component.scss',
})
export class RenameMaskFieldComponent {
  readonly label = input.required<string>();
  readonly testId = input.required<string>();
  readonly value = input.required<string>();
  readonly tokens = input.required<readonly RenameMaskToken[]>();
  readonly disabled = input(false);
  readonly valueChanged = output<string>();

  @ViewChild('editor', { read: ElementRef })
  private editor?: ElementRef<HTMLInputElement>;

  insertToken(token: string): void {
    if (this.disabled()) {
      return;
    }

    const editor = this.editor?.nativeElement;
    const start = editor?.selectionStart ?? this.value().length;
    const end = editor?.selectionEnd ?? start;
    const nextValue = `${this.value().slice(0, start)}${token}${this.value().slice(end)}`;
    this.valueChanged.emit(nextValue);
    if (editor) {
      editor.value = nextValue;
      editor.focus();
      editor.setSelectionRange(start + token.length, start + token.length);
    }
  }

  emitValue(event: Event): void {
    this.valueChanged.emit((event.target as HTMLInputElement).value);
  }
}
