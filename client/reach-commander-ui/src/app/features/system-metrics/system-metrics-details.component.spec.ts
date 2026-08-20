import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SystemMetricsDto } from '../../core/api/api.models';
import { SystemMetricsDetailsComponent } from './system-metrics-details.component';

describe('SystemMetricsDetailsComponent', () => {
  let fixture: ComponentFixture<SystemMetricsDetailsComponent>;
  let opener: HTMLButtonElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [SystemMetricsDetailsComponent] }).compileComponents();
    opener = document.createElement('button');
    document.body.appendChild(opener);
    fixture = TestBed.createComponent(SystemMetricsDetailsComponent);
    fixture.componentRef.setInput('snapshot', snapshot());
    fixture.componentRef.setInput('effectiveState', 'partial');
    fixture.componentRef.setInput('nowEpochMilliseconds', Date.parse('2026-08-19T12:00:05Z'));
    fixture.componentRef.setInput('opener', opener);
    fixture.detectChanges();
  });

  afterEach(() => opener.remove());

  it('renders every metric family and collector availability without inventing values', () => {
    const text = screenText();

    expect(text).toContain('System metrics');
    expect(text).toContain('CPU');
    expect(text).toContain('Memory');
    expect(text).toContain('Storage');
    expect(text).toContain('Graphics');
    expect(text).toContain('Fans');
    expect(text).toContain('Network');
    expect(text).toContain('Uptime');
    expect(text).toContain('unavailable');
    expect(text).toContain('—');
  });

  it('is a named modal dialog and auto-focuses its close button', () => {
    const dialog = fixture.nativeElement.querySelector('[role="dialog"]');
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(dialog.getAttribute('aria-labelledby')).toBe('system-metrics-title');
    expect(document.activeElement).toBe(fixture.nativeElement.querySelector('.close-button'));
  });

  it('installs CDK focus anchors to wrap keyboard tab navigation', () => {
    const anchors = fixture.nativeElement.querySelectorAll(
      '.cdk-focus-trap-anchor',
    ) as NodeListOf<HTMLElement>;

    expect(anchors).toHaveLength(2);
    expect(Array.from(anchors).every((anchor) => anchor.getAttribute('tabindex') === '0')).toBe(true);
  });

  it('closes on Escape and restores focus to the opener', () => {
    const closed = vi.fn();
    fixture.componentInstance.closed.subscribe(closed);
    opener.focus();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));

    expect(closed).toHaveBeenCalledOnce();
    expect(document.activeElement).toBe(opener);
  });

  it('closes from the backdrop and close button', () => {
    const closed = vi.fn();
    fixture.componentInstance.closed.subscribe(closed);

    (fixture.nativeElement.querySelector('.metrics-backdrop') as HTMLElement).click();
    expect(closed).toHaveBeenCalledTimes(1);

    (fixture.nativeElement.querySelector('.close-button') as HTMLButtonElement).click();
    expect(closed).toHaveBeenCalledTimes(2);
  });

  function screenText(): string {
    return fixture.nativeElement.textContent.replace(/\s+/g, ' ');
  }
});

function snapshot(): SystemMetricsDto {
  return {
    sampledAt: '2026-08-19T12:00:00Z',
    state: 'partial',
    hostUptimeSeconds: 3661,
    cpu: {
      utilizationPercent: 25,
      temperatureCelsius: null,
      warningTemperatureCelsius: 85,
      criticalTemperatureCelsius: 95,
      alarm: false,
      fault: false,
    },
    memory: { usedBytes: 60, availableBytes: 40, totalBytes: 100, utilizationPercent: 60 },
    storage: [
      { sourceId: 'media', name: 'Media', isAvailable: true, usedBytes: 75, freeBytes: 25, totalBytes: 100, utilizationPercent: 75 },
      { sourceId: 'usb', name: 'USB', isAvailable: false, usedBytes: null, freeBytes: null, totalBytes: null, utilizationPercent: null },
    ],
    gpus: [
      { id: 'integrated', vendor: 'Intel', name: 'Integrated', utilizationPercent: 5, memoryUsedBytes: null, memoryTotalBytes: null, temperatureCelsius: null, warningTemperatureCelsius: null, criticalTemperatureCelsius: null, alarm: false, fault: false },
      { id: 'discrete', vendor: 'NVIDIA', name: 'Discrete', utilizationPercent: 25, memoryUsedBytes: 2, memoryTotalBytes: 8, temperatureCelsius: 55, warningTemperatureCelsius: null, criticalTemperatureCelsius: null, alarm: false, fault: false },
    ],
    fans: [{ id: 'fan-001', name: 'CPU Fan', revolutionsPerMinute: 1400, alarm: false, fault: false }],
    network: { receiveBytesPerSecond: 1024, transmitBytesPerSecond: 512 },
    collectors: [{ collector: 'nvidia-nvml', state: 'unavailable', code: 'gpu_unavailable' }],
  };
}
