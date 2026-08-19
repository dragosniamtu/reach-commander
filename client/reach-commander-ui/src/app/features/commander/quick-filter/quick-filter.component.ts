import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

@Component({
  selector: 'app-quick-filter',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './quick-filter.component.html',
  styleUrl: './quick-filter.component.scss',
})
export class QuickFilterComponent {
  readonly value = input.required<string>();
  readonly valueChanged = output<string>();

  update(event: Event): void {
    this.valueChanged.emit((event.target as HTMLInputElement).value);
  }
}
