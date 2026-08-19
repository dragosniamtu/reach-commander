export type FileEntryType = 'file' | 'directory' | 'other';

export interface SourceDto {
  readonly id: string;
  readonly name: string;
  readonly isAvailable: boolean;
  readonly isReadOnly: boolean;
  readonly totalBytes: number | null;
  readonly usedBytes: number | null;
  readonly freeBytes: number | null;
  readonly defaultLeft: boolean;
  readonly defaultRight: boolean;
}

export interface FileEntryDto {
  readonly name: string;
  readonly relativePath: string;
  readonly type: FileEntryType;
  readonly size: number | null;
  readonly modifiedAt: string;
  readonly extension: string | null;
  readonly isReadOnly: boolean;
  readonly isSymbolicLink: boolean;
  readonly attributes: string;
}

export interface ApiProblemDetails {
  readonly type: string;
  readonly title: string;
  readonly status: number;
  readonly detail: string;
  readonly instance?: string;
  readonly code: string;
}

export type HardwareMetricsState = 'healthy' | 'partial' | 'stale' | 'disabled';
export type HardwareCollectorState = 'success' | 'unsupported' | 'unavailable' | 'timeout' | 'failed';

export interface CpuMetricsDto {
  readonly utilizationPercent: number | null;
  readonly temperatureCelsius: number | null;
  readonly warningTemperatureCelsius: number | null;
  readonly criticalTemperatureCelsius: number | null;
  readonly alarm: boolean;
  readonly fault: boolean;
}

export interface MemoryMetricsDto {
  readonly usedBytes: number | null;
  readonly availableBytes: number | null;
  readonly totalBytes: number | null;
  readonly utilizationPercent: number | null;
}

export interface StorageMetricsDto {
  readonly sourceId: string;
  readonly name: string;
  readonly isAvailable: boolean;
  readonly usedBytes: number | null;
  readonly freeBytes: number | null;
  readonly totalBytes: number | null;
  readonly utilizationPercent: number | null;
}

export interface GpuMetricsDto {
  readonly id: string;
  readonly vendor: string;
  readonly name: string;
  readonly utilizationPercent: number | null;
  readonly memoryUsedBytes: number | null;
  readonly memoryTotalBytes: number | null;
  readonly temperatureCelsius: number | null;
  readonly warningTemperatureCelsius: number | null;
  readonly criticalTemperatureCelsius: number | null;
  readonly alarm: boolean;
  readonly fault: boolean;
}

export interface FanMetricsDto {
  readonly id: string;
  readonly name: string;
  readonly revolutionsPerMinute: number | null;
  readonly alarm: boolean;
  readonly fault: boolean;
}

export interface NetworkMetricsDto {
  readonly receiveBytesPerSecond: number | null;
  readonly transmitBytesPerSecond: number | null;
}

export interface HardwareCollectorStatusDto {
  readonly collector: string;
  readonly state: HardwareCollectorState;
  readonly code: string | null;
}

export interface SystemMetricsDto {
  readonly sampledAt: string;
  readonly state: HardwareMetricsState;
  readonly hostUptimeSeconds: number | null;
  readonly cpu: CpuMetricsDto | null;
  readonly memory: MemoryMetricsDto | null;
  readonly storage: readonly StorageMetricsDto[];
  readonly gpus: readonly GpuMetricsDto[];
  readonly fans: readonly FanMetricsDto[];
  readonly network: NetworkMetricsDto | null;
  readonly collectors: readonly HardwareCollectorStatusDto[];
}

export abstract class CommanderApiPort {
  abstract getSystemMetrics(): Promise<SystemMetricsDto>;

  abstract getSources(): Promise<readonly SourceDto[]>;

  abstract listFiles(sourceId: string, path: string): Promise<readonly FileEntryDto[]>;

  abstract getInfo(sourceId: string, path: string): Promise<FileEntryDto>;
}
