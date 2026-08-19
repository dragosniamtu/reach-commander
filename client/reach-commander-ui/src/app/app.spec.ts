import { TestBed } from '@angular/core/testing';
import { App } from './app';
import {
  CommanderApiPort,
  FileEntryDto,
  SourceDto,
  SystemMetricsDto,
} from './core/api/api.models';

describe('App', () => {
  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [{ provide: CommanderApiPort, useClass: AppTestApi }],
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  it('renders the ReachCommander dual-pane shell', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.textContent).toContain('ReachCommander');
    expect(compiled.querySelectorAll('app-commander-panel')).toHaveLength(2);
  });
});

class AppTestApi extends CommanderApiPort {
  async getSystemMetrics(): Promise<SystemMetricsDto> {
    return {
      sampledAt: new Date().toISOString(),
      state: 'disabled',
      hostUptimeSeconds: null,
      cpu: null,
      memory: null,
      storage: [],
      gpus: [],
      fans: [],
      network: null,
      collectors: [],
    };
  }

  async getSources(): Promise<readonly SourceDto[]> {
    return [{
      id: 'downloads',
      name: 'Downloads',
      isAvailable: true,
      isReadOnly: false,
      totalBytes: 100,
      usedBytes: 25,
      freeBytes: 75,
      defaultLeft: true,
      defaultRight: true,
    }];
  }

  async listFiles(): Promise<readonly FileEntryDto[]> {
    return [];
  }

  async getInfo(): Promise<FileEntryDto> {
    throw new Error('Not used');
  }
}
