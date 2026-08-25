import { A11yModule } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  HostListener,
  input,
  output,
} from '@angular/core';
import { SystemUpdateStatusDto } from '../../core/api/api.models';

@Component({
  selector: 'app-system-update-dialog',
  imports: [A11yModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './system-update-dialog.component.html',
  styleUrl: './system-update-dialog.component.scss',
})
export class SystemUpdateDialogComponent {
  readonly status = input.required<SystemUpdateStatusDto>();
  readonly opener = input.required<HTMLElement | null>();
  readonly apply = output<void>();
  readonly closed = output<void>();

  confirm(): void {
    if (this.status().canApply) {
      this.apply.emit();
    }
  }

  close(): void {
    this.closed.emit();
    this.opener()?.focus();
  }

  @HostListener('keydown', ['$event'])
  handleKeydown(event: KeyboardEvent): void {
    event.stopPropagation();
    if (event.key === 'Escape') {
      event.preventDefault();
      this.close();
    }
  }
}
