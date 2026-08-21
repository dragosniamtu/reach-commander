import {
  ChangeDetectionStrategy,
  Component,
  inject,
  input,
  output,
} from '@angular/core';
import { PwaService } from '../../core/pwa/pwa.service';

@Component({
  selector: 'app-pwa-status',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pwa-status.component.html',
  styleUrl: './pwa-status.component.scss',
})
export class PwaStatusComponent {
  readonly pwa = inject(PwaService);
  readonly initializationError = input<string | null>(null);
  readonly retryRequested = output<void>();
}
