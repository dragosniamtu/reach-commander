import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  ViewChild,
  computed,
  effect,
  input,
  output,
  signal,
} from '@angular/core';
import {
  CpuMetricsDto,
  GpuMetricsDto,
  HardwareMetricsState,
  SystemMetricsDto,
} from '../../core/api/api.models';

type MetricsSeverity = 'neutral' | 'warning' | 'danger';

@Component({
  selector: 'app-system-metrics-widget',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './system-metrics-widget.component.html',
  styleUrl: './system-metrics-widget.component.scss',
})
export class SystemMetricsWidgetComponent {
  readonly snapshot = input.required<SystemMetricsDto | null>();
  readonly effectiveState = input.required<HardwareMetricsState | 'loading'>();
  readonly expanded = input(false);
  readonly openDetails = output<void>();

  @ViewChild('trigger', { read: ElementRef }) private trigger?: ElementRef<HTMLButtonElement>;

  readonly storageUtilization = computed(() => maximum(
    this.snapshot()?.storage.map((storage) => storage.utilizationPercent) ?? [],
  ));
  readonly gpuUtilization = computed(() => maximum(
    this.snapshot()?.gpus.map((gpu) => gpu.utilizationPercent) ?? [],
  ));
  readonly severity = computed<MetricsSeverity>(() => this.calculateSeverity());
  readonly announcement = signal('');
  readonly ariaSummary = computed(() => {
    const state = this.effectiveState();
    const snapshot = this.snapshot();
    if (!snapshot) {
      return `System metrics: ${state}. Loading hardware readings.`;
    }

    const cpu = this.cpuLabel(snapshot.cpu);
    return `System metrics: ${state}. ${cpu}. RAM ${percent(snapshot.memory?.utilizationPercent ?? null)}. ` +
      `Storage ${percent(this.storageUtilization())}. GPU ${percent(this.gpuUtilization())}.`;
  });

  private previousState: HardwareMetricsState | 'loading' | undefined;
  private previousSeverity: MetricsSeverity = 'neutral';

  constructor() {
    effect(() => {
      const state = this.effectiveState();
      const severity = this.severity();
      let message = '';

      if (this.previousState !== undefined && state !== this.previousState) {
        message = stateMessage(state, this.previousState);
      } else if (severity !== this.previousSeverity && severity !== 'neutral') {
        message = severity === 'danger'
          ? 'System metrics reached a critical level.'
          : 'System metrics reached a warning level.';
      }

      this.previousState = state;
      this.previousSeverity = severity;
      if (message) {
        this.announcement.set(message);
      }
    });
  }

  focusTrigger(): void {
    this.trigger?.nativeElement.focus();
  }

  get triggerElement(): HTMLButtonElement | null {
    return this.trigger?.nativeElement ?? null;
  }

  cpuLabel(cpu: CpuMetricsDto | null): string {
    if (!cpu) {
      return 'CPU —';
    }

    const utilization = percent(cpu.utilizationPercent);
    return cpu.temperatureCelsius === null
      ? `CPU ${utilization}`
      : `CPU ${utilization} · ${temperature(cpu.temperatureCelsius)}`;
  }

  stateLabel(): string {
    const state = this.effectiveState();
    return state.charAt(0).toUpperCase() + state.slice(1);
  }

  percent(value: number | null | undefined): string {
    return percent(value ?? null);
  }

  private calculateSeverity(): MetricsSeverity {
    const snapshot = this.snapshot();
    if (!snapshot) {
      return 'neutral';
    }

    const levels = [
      utilizationSeverity(snapshot.cpu?.utilizationPercent ?? null),
      utilizationSeverity(snapshot.memory?.utilizationPercent ?? null),
      utilizationSeverity(this.storageUtilization()),
      utilizationSeverity(this.gpuUtilization()),
      cpuSeverity(snapshot.cpu),
      ...snapshot.gpus.map(gpuSeverity),
      snapshot.fans.some((fan) => fan.alarm || fan.fault) ? 'danger' as const : 'neutral' as const,
    ];
    return levels.reduce((highest, level) =>
      severityRank(level) > severityRank(highest) ? level : highest, 'neutral' as MetricsSeverity);
  }
}

function maximum(values: readonly (number | null)[]): number | null {
  const present = values.filter((value): value is number => value !== null);
  return present.length === 0 ? null : Math.max(...present);
}

function percent(value: number | null): string {
  return value === null ? '—' : `${Math.round(value)}%`;
}

function temperature(value: number): string {
  return `${Math.round(value)}°C`;
}

function utilizationSeverity(value: number | null): MetricsSeverity {
  if (value === null) {
    return 'neutral';
  }
  if (value >= 95) {
    return 'danger';
  }
  return value >= 80 ? 'warning' : 'neutral';
}

function cpuSeverity(cpu: CpuMetricsDto | null): MetricsSeverity {
  return temperatureSeverity(cpu);
}

function gpuSeverity(gpu: GpuMetricsDto): MetricsSeverity {
  const temperatureLevel = temperatureSeverity(gpu);
  const utilizationLevel = utilizationSeverity(gpu.utilizationPercent);
  return severityRank(temperatureLevel) > severityRank(utilizationLevel)
    ? temperatureLevel
    : utilizationLevel;
}

function temperatureSeverity(device: {
  readonly temperatureCelsius: number | null;
  readonly warningTemperatureCelsius: number | null;
  readonly criticalTemperatureCelsius: number | null;
  readonly alarm: boolean;
  readonly fault: boolean;
} | null): MetricsSeverity {
  if (!device) {
    return 'neutral';
  }
  if (device.alarm || device.fault) {
    return 'danger';
  }
  if (device.temperatureCelsius !== null &&
      device.criticalTemperatureCelsius !== null &&
      device.temperatureCelsius >= device.criticalTemperatureCelsius) {
    return 'danger';
  }
  if (device.temperatureCelsius !== null &&
      device.warningTemperatureCelsius !== null &&
      device.temperatureCelsius >= device.warningTemperatureCelsius) {
    return 'warning';
  }
  return 'neutral';
}

function severityRank(severity: MetricsSeverity): number {
  return severity === 'danger' ? 2 : severity === 'warning' ? 1 : 0;
}

function stateMessage(
  state: HardwareMetricsState | 'loading',
  previous: HardwareMetricsState | 'loading',
): string {
  switch (state) {
    case 'healthy': return previous === 'healthy' ? '' : 'System metrics recovered and are healthy.';
    case 'partial': return 'System metrics are partially available.';
    case 'stale': return 'System metrics are stale.';
    case 'disabled': return 'System metrics are disabled.';
    default: return 'System metrics are loading.';
  }
}
