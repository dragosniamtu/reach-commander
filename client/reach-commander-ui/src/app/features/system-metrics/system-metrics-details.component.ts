import {
  ChangeDetectionStrategy,
  Component,
  AfterViewInit,
  ElementRef,
  HostListener,
  ViewChild,
  computed,
  input,
  output,
} from '@angular/core';
import { A11yModule } from '@angular/cdk/a11y';
import { HardwareMetricsState, SystemMetricsDto } from '../../core/api/api.models';
import { ByteRatePipe } from '../../shared/pipes/byte-rate.pipe';
import { FileSizePipe } from '../../shared/pipes/file-size.pipe';

@Component({
  selector: 'app-system-metrics-details',
  imports: [A11yModule, ByteRatePipe, FileSizePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './system-metrics-details.component.html',
  styleUrl: './system-metrics-details.component.scss',
})
export class SystemMetricsDetailsComponent implements AfterViewInit {
  readonly snapshot = input.required<SystemMetricsDto | null>();
  readonly effectiveState = input.required<HardwareMetricsState | 'loading'>();
  readonly nowEpochMilliseconds = input.required<number>();
  readonly opener = input.required<HTMLElement | null>();
  readonly closed = output<void>();

  @ViewChild('closeButton', { read: ElementRef }) private closeButton?: ElementRef<HTMLButtonElement>;

  readonly unavailableCollectors = computed(() =>
    this.snapshot()?.collectors.filter((collector) => collector.state !== 'success') ?? [],
  );
  readonly sampledAge = computed(() => {
    const sampledAt = this.snapshot()?.sampledAt;
    if (!sampledAt) {
      return '—';
    }
    const ageSeconds = Math.max(0, Math.floor(
      (this.nowEpochMilliseconds() - Date.parse(sampledAt)) / 1000,
    ));
    return Number.isFinite(ageSeconds) ? `${ageSeconds}s ago` : '—';
  });

  ngAfterViewInit(): void {
    this.closeButton?.nativeElement.focus();
  }

  @HostListener('document:keydown.escape', ['$event'])
  closeFromEscape(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.close();
  }

  close(): void {
    this.closed.emit();
    this.opener()?.focus();
  }

  backdropClick(event: MouseEvent): void {
    if (event.target === event.currentTarget) {
      this.close();
    }
  }

  percentage(value: number | null | undefined): string {
    return value === null || value === undefined ? '—' : `${Math.round(value)}%`;
  }

  temperature(value: number | null | undefined): string {
    return value === null || value === undefined ? '—' : `${Math.round(value)}°C`;
  }

  uptime(seconds: number | null | undefined): string {
    if (seconds === null || seconds === undefined) {
      return '—';
    }
    const days = Math.floor(seconds / 86_400);
    const hours = Math.floor((seconds % 86_400) / 3_600);
    const minutes = Math.floor((seconds % 3_600) / 60);
    return [days ? `${days}d` : '', hours ? `${hours}h` : '', `${minutes}m`]
      .filter(Boolean)
      .join(' ');
  }
}
