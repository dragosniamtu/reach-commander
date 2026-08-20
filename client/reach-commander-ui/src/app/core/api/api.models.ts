import { Observable } from 'rxjs';

export type FileEntryType = 'file' | 'directory' | 'other';
export type ArchiveFormat = 'zip' | 'rar' | 'sevenZip';
export type ArchiveRole = 'single' | 'primary' | 'secondary';

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
  readonly modifiedAt: string | null;
  readonly extension: string | null;
  readonly isReadOnly: boolean;
  readonly isSymbolicLink: boolean;
  readonly attributes: string;
  readonly archiveFormatHint: ArchiveFormat | null;
  readonly archiveRole: ArchiveRole | null;
}

export interface ArchiveDirectoryDto {
  readonly sourceId: string;
  readonly archivePath: string;
  readonly path: string;
  readonly format: ArchiveFormat;
  readonly volumeCount: number;
  readonly isReadOnly: true;
  readonly entries: readonly FileEntryDto[];
}

export interface ApiProblemDetails {
  readonly type: string;
  readonly title: string;
  readonly status: number;
  readonly detail: string;
  readonly instance?: string;
  readonly code: string;
}

export interface UploadLimitsDto {
  readonly maxFileBytes: number;
  readonly maxBatchBytes: number;
  readonly maxFilesPerBatch: number;
}

export interface UploadedFileDto {
  readonly name: string;
  readonly relativePath: string;
  readonly size: number;
}

export interface UploadResultDto {
  readonly uploadedCount: number;
  readonly totalBytes: number;
  readonly files: readonly UploadedFileDto[];
}

export interface UploadTarget {
  readonly sourceId: string;
  readonly directoryPath: string;
}

export type BatchRenameCaseMode =
  'unchanged' | 'lowercase' | 'uppercase' | 'capitalizeWords' | 'sentenceCase';
export type BatchRenamePreviewStatus = 'ready' | 'unchanged' | 'invalid' | 'conflict' | 'stale';
export type BatchRenameOperationStatus = 'completed' | 'failed' | 'recoveryRequired' | 'undone';
export type BatchRenameRowResult =
  'completed' | 'unchanged' | 'failed' | 'rolledBack' | 'recoveryRequired';

export interface BatchRenameRulesDto {
  readonly nameMask: string;
  readonly extensionMask: string;
  readonly searchFor: string;
  readonly replaceWith: string;
  readonly useRegex: boolean;
  readonly matchCase: boolean;
  readonly replaceInExtension: boolean;
  readonly caseMode: BatchRenameCaseMode;
  readonly counterStart: number;
  readonly counterStep: number;
  readonly counterDigits: number;
}

export interface BatchRenamePreviewRequestDto {
  readonly sourceId: string;
  readonly directoryPath: string;
  readonly entryPaths: readonly string[];
  readonly rules: BatchRenameRulesDto;
}

export interface BatchRenamePreviewRowDto {
  readonly sourcePath: string;
  readonly oldName: string;
  readonly oldExtension: string | null;
  readonly newName: string;
  readonly type: FileEntryType;
  readonly size: number | null;
  readonly modifiedAt: string;
  readonly status: BatchRenamePreviewStatus;
  readonly message: string | null;
}

export interface BatchRenamePreviewDto {
  readonly planId: string;
  readonly expiresAt: string;
  readonly rows: readonly BatchRenamePreviewRowDto[];
  readonly canExecute: boolean;
  readonly changedCount: number;
  readonly unchangedCount: number;
  readonly invalidCount: number;
}

export interface BatchRenameOperationRowDto {
  readonly oldPath: string;
  readonly newPath: string;
  readonly currentPath: string;
  readonly oldName: string;
  readonly newName: string;
  readonly currentName: string;
  readonly type: FileEntryType;
  readonly result: BatchRenameRowResult;
  readonly message: string | null;
}

export interface BatchRenameOperationDto {
  readonly operationId: string;
  readonly status: BatchRenameOperationStatus;
  readonly rows: readonly BatchRenameOperationRowDto[];
  readonly compensationAttempted: boolean;
  readonly recoveryRequired: boolean;
  readonly undoAvailable: boolean;
  readonly undoExpiresAt: string | null;
}

export type UploadEvent =
  | {
      readonly kind: 'progress';
      readonly loadedBytes: number;
      readonly totalBytes: number | null;
    }
  | {
      readonly kind: 'completed';
      readonly result: UploadResultDto;
    };

export type HardwareMetricsState = 'healthy' | 'partial' | 'stale' | 'disabled';
export type HardwareCollectorState =
  'success' | 'unsupported' | 'unavailable' | 'timeout' | 'failed';

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

  abstract listArchive(
    sourceId: string,
    archivePath: string,
    internalPath: string,
  ): Promise<ArchiveDirectoryDto>;

  abstract getInfo(sourceId: string, path: string): Promise<FileEntryDto>;

  abstract getUploadLimits(): Promise<UploadLimitsDto>;

  abstract uploadFiles(target: UploadTarget, files: readonly File[]): Observable<UploadEvent>;

  abstract previewBatchRename(
    request: BatchRenamePreviewRequestDto,
  ): Promise<BatchRenamePreviewDto>;

  abstract executeBatchRename(planId: string): Promise<BatchRenameOperationDto>;

  abstract undoBatchRename(operationId: string): Promise<BatchRenameOperationDto>;
}
