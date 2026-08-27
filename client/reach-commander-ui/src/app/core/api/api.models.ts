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

export interface ExactRenamePreviewRequestDto {
  readonly sourceId: string;
  readonly directoryPath: string;
  readonly entryPath: string;
  readonly newName: string;
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

export interface ArchiveExtractionPreviewRequestDto {
  readonly sourceId: string;
  readonly archivePath: string;
  readonly internalDirectory: string;
  readonly entryPaths: readonly string[];
  readonly extractAll: boolean;
  readonly destinationSourceId: string;
  readonly destinationPath: string;
}

export interface ArchiveExtractionIssueDto {
  readonly code: string;
  readonly message: string;
  readonly logicalPaths: readonly string[];
}

export interface ArchiveExtractionPreviewDto {
  readonly planId: string;
  readonly expiresAt: string;
  readonly format: ArchiveFormat;
  readonly volumeCount: number;
  readonly selectedRoots: readonly string[];
  readonly fileCount: number;
  readonly directoryCount: number;
  readonly totalExtractedBytes: number | null;
  readonly destinationSourceId: string;
  readonly destinationPath: string;
  readonly conflicts: readonly ArchiveExtractionIssueDto[];
  readonly violations: readonly ArchiveExtractionIssueDto[];
  readonly canExecute: boolean;
}

export type ArchiveExtractionOperationState =
  'queued' | 'extracting' | 'finalizing' | 'completed' | 'cancelled' | 'failed' |
  'recoveryRequired';
export type ArchiveExtractionCompensationState =
  'notRequired' | 'notStarted' | 'succeeded' | 'failed';

export interface ArchiveExtractionOperationDto {
  readonly operationId: string;
  readonly state: ArchiveExtractionOperationState;
  readonly completedFiles: number;
  readonly totalFiles: number;
  readonly extractedBytes: number;
  readonly totalBytes: number | null;
  readonly percent: number | null;
  readonly currentEntryName: string | null;
  readonly canCancel: boolean;
  readonly compensationState: ArchiveExtractionCompensationState;
  readonly recoveryNames: readonly string[];
  readonly errorCode: string | null;
  readonly errorDetail: string | null;
}

export type FileOperationKind =
  'copy' | 'move' | 'permanentDelete' | 'trash' | 'restore' | 'emptyTrash';
export type FileOperationConflictDecision = 'overwrite' | 'skip' | 'createUniqueName';
export type FileOperationPhase =
  'queued' | 'validating' | 'running' | 'cancelling' | 'completed' |
  'completedWithErrors' | 'cancelled' | 'failed' | 'interrupted';
export type FileOperationItemResult =
  'completed' | 'skipped' | 'failed' | 'copiedButNotRemoved' | 'notStarted';

export interface FileOperationPreviewRequestDto {
  readonly kind: 'copy' | 'move';
  readonly sourceId: string;
  readonly logicalPaths: readonly string[];
  readonly destinationSourceId: string;
  readonly destinationLogicalDirectory: string;
}

export interface FileOperationConflictDto {
  readonly conflictId: string;
  readonly sourceLogicalPath: string;
  readonly destinationLogicalPath: string;
  readonly sourceType: FileEntryType;
  readonly destinationType: FileEntryType;
  readonly allowedDecisions: readonly FileOperationConflictDecision[];
}

export interface FileOperationPreviewDto extends FileOperationPreviewRequestDto {
  readonly planId: string;
  readonly expiresAt: string;
  readonly totalItems: number;
  readonly totalBytes: number | null;
  readonly conflicts: readonly FileOperationConflictDto[];
  readonly warnings: readonly string[];
}

export interface FileOperationConflictResolutionDto {
  readonly conflictId: string;
  readonly decision: FileOperationConflictDecision;
}

export interface FileOperationSubmissionDto {
  readonly planId: string;
  readonly resolutions: readonly FileOperationConflictResolutionDto[];
}

export interface FileOperationProgressDto {
  readonly currentLogicalName: string | null;
  readonly completedItems: number;
  readonly totalItems: number;
  readonly completedBytes: number;
  readonly totalBytes: number | null;
  readonly percentage: number | null;
  readonly bytesPerSecond: number | null;
  readonly elapsed: string;
  readonly estimatedRemaining: string | null;
}

export interface FileOperationItemOutcomeDto {
  readonly sourceId: string;
  readonly sourceLogicalPath: string;
  readonly destinationSourceId: string | null;
  readonly destinationLogicalPath: string | null;
  readonly result: FileOperationItemResult;
  readonly errorCode: string | null;
  readonly detail: string | null;
}

export interface FileOperationStatusDto {
  readonly operationId: string;
  readonly kind: FileOperationKind;
  readonly phase: FileOperationPhase;
  readonly queuePosition: number;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly progress: FileOperationProgressDto;
  readonly outcomes: readonly FileOperationItemOutcomeDto[];
  readonly warnings: readonly string[];
  readonly acknowledged: boolean;
}

export interface CreateDirectoryRequestDto {
  readonly sourceId: string;
  readonly parentLogicalPath: string;
  readonly name: string;
}

export interface DeletePreviewRequestDto {
  readonly sourceId: string;
  readonly logicalPaths: readonly string[];
  readonly mode: 'trash' | 'permanent';
}

export interface DeletePreviewDto {
  readonly planId: string;
  readonly expiresAt: string;
  readonly mode: 'trash' | 'permanent';
  readonly trashAvailable: boolean;
  readonly trashUnavailableReason: string | null;
  readonly totalItems: number;
  readonly totalBytes: number | null;
}

export interface DeleteSubmissionDto {
  readonly planId: string;
  readonly permanentDeleteConfirmed: boolean;
}

export interface TrashEntryDto {
  readonly trashId: string;
  readonly sourceId: string;
  readonly originalLogicalPath: string;
  readonly name: string;
  readonly type: FileEntryType;
  readonly size: number | null;
  readonly deletedAt: string;
}

export interface RestorePreviewRequestDto {
  readonly trashIds: readonly string[];
}

export interface RestorePreviewDto {
  readonly planId: string;
  readonly expiresAt: string;
  readonly entries: readonly TrashEntryDto[];
  readonly conflicts: readonly FileOperationConflictDto[];
  readonly parentsToCreate: readonly string[];
}

export interface RestoreSubmissionDto {
  readonly planId: string;
  readonly resolutions: readonly FileOperationConflictResolutionDto[];
}

export interface TrashPermanentDeleteRequestDto {
  readonly trashIds: readonly string[];
  readonly permanentDeleteConfirmed: boolean;
}

export interface EmptyTrashRequestDto {
  readonly sourceId: string | null;
  readonly permanentDeleteConfirmed: boolean;
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

export type SystemUpdatePhase =
  | 'unavailable'
  | 'checking'
  | 'current'
  | 'available'
  | 'blocked'
  | 'applying'
  | 'completed'
  | 'rolledBack'
  | 'failed';

export type SystemUpdateProgressStage =
  | 'downloading'
  | 'installing'
  | 'restarting'
  | 'healthChecking'
  | 'restoring'
  | 'restartingPrevious'
  | 'verifyingRecovery';

export interface SystemUpdateStatusDto {
  readonly protocolVersion: number;
  readonly supported: boolean;
  readonly channel: string | null;
  readonly currentVersion: string | null;
  readonly targetVersion: string | null;
  readonly phase: SystemUpdatePhase;
  readonly progressStage: SystemUpdateProgressStage | null;
  readonly updateAvailable: boolean;
  readonly canApply: boolean;
  readonly reasonCode: string | null;
  readonly detail: string | null;
  readonly operationId: string | null;
  readonly lastCheckedAt: string | null;
  readonly updatedAt: string;
}

export abstract class CommanderApiPort {
  abstract getSystemMetrics(): Promise<SystemMetricsDto>;

  abstract getSystemUpdate(): Promise<SystemUpdateStatusDto>;

  abstract checkSystemUpdate(): Promise<SystemUpdateStatusDto>;

  abstract applySystemUpdate(): Promise<SystemUpdateStatusDto>;

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

  abstract previewRename(
    request: ExactRenamePreviewRequestDto,
  ): Promise<BatchRenamePreviewDto>;

  abstract executeBatchRename(planId: string): Promise<BatchRenameOperationDto>;

  abstract undoBatchRename(operationId: string): Promise<BatchRenameOperationDto>;

  abstract previewArchiveExtraction(
    request: ArchiveExtractionPreviewRequestDto,
  ): Promise<ArchiveExtractionPreviewDto>;

  abstract executeArchiveExtraction(planId: string): Promise<ArchiveExtractionOperationDto>;

  abstract getArchiveExtraction(operationId: string): Promise<ArchiveExtractionOperationDto>;

  abstract cancelArchiveExtraction(operationId: string): Promise<ArchiveExtractionOperationDto>;

  abstract previewFileOperation(
    request: FileOperationPreviewRequestDto,
  ): Promise<FileOperationPreviewDto>;

  abstract submitFileOperation(
    request: FileOperationSubmissionDto,
  ): Promise<FileOperationStatusDto>;

  abstract listFileOperations(): Promise<readonly FileOperationStatusDto[]>;

  abstract getFileOperation(operationId: string): Promise<FileOperationStatusDto>;

  abstract cancelFileOperation(operationId: string): Promise<FileOperationStatusDto>;

  abstract acknowledgeFileOperation(operationId: string): Promise<void>;

  abstract createDirectory(request: CreateDirectoryRequestDto): Promise<FileEntryDto>;

  abstract previewDelete(request: DeletePreviewRequestDto): Promise<DeletePreviewDto>;

  abstract submitDelete(request: DeleteSubmissionDto): Promise<FileOperationStatusDto>;

  abstract listTrash(sourceId?: string): Promise<readonly TrashEntryDto[]>;

  abstract previewRestore(request: RestorePreviewRequestDto): Promise<RestorePreviewDto>;

  abstract submitRestore(request: RestoreSubmissionDto): Promise<FileOperationStatusDto>;

  abstract permanentlyDeleteTrash(
    request: TrashPermanentDeleteRequestDto,
  ): Promise<FileOperationStatusDto>;

  abstract emptyTrash(request: EmptyTrashRequestDto): Promise<FileOperationStatusDto>;
}
