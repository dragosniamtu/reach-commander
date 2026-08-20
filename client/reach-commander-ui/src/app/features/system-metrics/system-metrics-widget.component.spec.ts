import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  CpuMetricsDto,
  GpuMetricsDto,
  MemoryMetricsDto,
  StorageMetricsDto,
  SystemMetricsDto,
} from '../../core/api/api.models';
import { SystemMetricsWidgetComponent } from './system-metrics-widget.component';

describe('SystemMetricsWidgetComponent', () => {
  let fixture: ComponentFixture<SystemMetricsWidgetComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [SystemMetricsWidgetComponent] }).compileComponents();
    fixture = TestBed.createComponent(SystemMetricsWidgetComponent);
    fixture.componentRef.setInput('effectiveState', 'healthy');
    fixture.componentRef.setInput('expanded', false);
  });

  it('shows the compact core summary using the fullest source and busiest GPU', () => {
    fixture.componentRef.setInput('snapshot', systemMetricsResponse({
      cpu: cpu({ utilizationPercent: 18, temperatureCelsius: 54 }),
      memory: memory({ utilizationPercent: 43 }),
      storage: [storage('downloads', 52), storage('media', 71)],
      gpus: [gpu('integrated', 4), gpu('discrete', 12)],
    }));
    fixture.detectChanges();

    expect(summary()).toContain('CPU 18% · 54°C');
    expect(summary()).toContain('RAM 43%');
    expect(summary()).toContain('STORAGE 71%');
    expect(summary()).toContain('GPU 12%');
  });

  it('renders unavailable values as em dashes and exposes the server state', () => {
    fixture.componentRef.setInput('snapshot', systemMetricsResponse({
      state: 'partial', cpu: null, memory: null, storage: [], gpus: [],
    }));
    fixture.componentRef.setInput('effectiveState', 'partial');
    fixture.detectChanges();

    expect(summary()).toContain('CPU —');
    expect(summary()).toContain('RAM —');
    expect(button().getAttribute('data-state')).toBe('partial');
    expect(button().getAttribute('aria-label')).toContain('System metrics: partial');
  });

  it('shows stable loading and stale labels and emits open details', () => {
    const opened = vi.fn();
    fixture.componentInstance.openDetails.subscribe(opened);
    fixture.componentRef.setInput('snapshot', null);
    fixture.componentRef.setInput('effectiveState', 'loading');
    fixture.detectChanges();

    expect(summary()).toContain('System · Loading');
    expect(button().getAttribute('aria-expanded')).toBe('false');

    fixture.componentRef.setInput('expanded', true);
    fixture.componentRef.setInput('effectiveState', 'stale');
    fixture.detectChanges();
    expect(summary()).toContain('System · Stale');
    expect(button().getAttribute('aria-expanded')).toBe('true');
    button().click();
    expect(opened).toHaveBeenCalledOnce();
  });

  it('uses danger presentation for alarm data below numeric thresholds', () => {
    fixture.componentRef.setInput('snapshot', systemMetricsResponse({
      cpu: cpu({ utilizationPercent: 12, temperatureCelsius: 35, alarm: true }),
    }));
    fixture.detectChanges();

    expect(button().getAttribute('data-severity')).toBe('danger');
  });

  it('announces semantic transitions but ignores numeric-only updates', () => {
    fixture.componentRef.setInput('snapshot', systemMetricsResponse());
    fixture.detectChanges();
    const initial = announcer();

    fixture.componentRef.setInput('snapshot', systemMetricsResponse({
      cpu: cpu({ utilizationPercent: 30 }),
    }));
    fixture.detectChanges();
    expect(announcer()).toBe(initial);

    fixture.componentRef.setInput('effectiveState', 'partial');
    fixture.detectChanges();
    expect(announcer()).toContain('partially');

    fixture.componentRef.setInput('effectiveState', 'healthy');
    fixture.detectChanges();
    expect(announcer()).toContain('recovered');

    fixture.componentRef.setInput('snapshot', systemMetricsResponse({
      cpu: cpu({ utilizationPercent: 82 }),
    }));
    fixture.detectChanges();
    expect(announcer()).toContain('warning');

    fixture.componentRef.setInput('snapshot', systemMetricsResponse({
      cpu: cpu({ utilizationPercent: 97 }),
    }));
    fixture.detectChanges();
    expect(announcer()).toContain('critical');

    fixture.componentRef.setInput('effectiveState', 'stale');
    fixture.detectChanges();
    expect(announcer()).toContain('stale');
  });

  function button(): HTMLButtonElement {
    return fixture.nativeElement.querySelector('[data-testid="system-metrics-trigger"]');
  }

  function summary(): string {
    return fixture.nativeElement.querySelector('.metrics-summary').textContent;
  }

  function announcer(): string {
    return fixture.nativeElement.querySelector('[aria-live]').textContent.trim();
  }
});

function systemMetricsResponse(overrides: Partial<SystemMetricsDto> = {}): SystemMetricsDto {
  return {
    sampledAt: '2026-08-19T12:00:00Z',
    state: 'healthy',
    hostUptimeSeconds: 3600,
    cpu: cpu(),
    memory: memory(),
    storage: [storage('media', 50)],
    gpus: [],
    fans: [],
    network: null,
    collectors: [],
    ...overrides,
  };
}

function cpu(overrides: Partial<CpuMetricsDto> = {}): CpuMetricsDto {
  return {
    utilizationPercent: 20,
    temperatureCelsius: null,
    warningTemperatureCelsius: 85,
    criticalTemperatureCelsius: 95,
    alarm: false,
    fault: false,
    ...overrides,
  };
}

function memory(overrides: Partial<MemoryMetricsDto> = {}): MemoryMetricsDto {
  return {
    usedBytes: 50,
    availableBytes: 50,
    totalBytes: 100,
    utilizationPercent: 50,
    ...overrides,
  };
}

function storage(id: string, utilizationPercent: number): StorageMetricsDto {
  return { sourceId: id, name: id, isAvailable: true, usedBytes: 50, freeBytes: 50, totalBytes: 100, utilizationPercent };
}

function gpu(id: string, utilizationPercent: number): GpuMetricsDto {
  return {
    id,
    vendor: 'Test',
    name: id,
    utilizationPercent,
    memoryUsedBytes: null,
    memoryTotalBytes: null,
    temperatureCelsius: null,
    warningTemperatureCelsius: null,
    criticalTemperatureCelsius: null,
    alarm: false,
    fault: false,
  };
}
