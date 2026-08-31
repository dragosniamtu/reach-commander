import {
  HttpClient,
  HttpEvent,
  HttpEventType,
  HttpParams,
  HttpRequest,
  HttpResponse,
  HttpUploadProgressEvent,
} from '@angular/common/http';
import { Injectable } from '@angular/core';
import { filter, firstValueFrom, map, Observable } from 'rxjs';
import {
  CommanderApiPort,
  ArchiveDirectoryDto,
  ArchiveFormat,
  ArchiveExtractionOperationDto,
  ArchiveExtractionPreviewDto,
  ArchiveExtractionPreviewRequestDto,
  BatchRenameOperationDto,
  BatchRenamePreviewDto,
  BatchRenamePreviewRequestDto,
  CreateDirectoryRequestDto,
  DeletePreviewDto,
  DeletePreviewRequestDto,
  DeleteSubmissionDto,
  EmptyTrashRequestDto,
  ExactRenamePreviewRequestDto,
  FileEntryDto,
  FileOperationPreviewDto,
  FileOperationPreviewRequestDto,
  FileOperationStatusDto,
  FileOperationSubmissionDto,
  RestorePreviewDto,
  RestorePreviewRequestDto,
  RestoreSubmissionDto,
  SourceAddRequestDto,
  SourceDto,
  SourceManagementCapabilityDto,
  SourceManagementOperationDto,
  SystemMetricsDto,
  SystemUpdateStatusDto,
  SystemUpdateSupportBundleDownload,
  UploadEvent,
  UploadLimitsDto,
  UploadResultDto,
  UploadTarget,
  TrashEntryDto,
  TrashPermanentDeleteRequestDto,
} from './api.models';

@Injectable({ providedIn: 'root' })
export class ReachCommanderApi extends CommanderApiPort {
  constructor(private readonly http: HttpClient) {
    super();
  }

  getSystemMetrics(): Promise<SystemMetricsDto> {
    return firstValueFrom(this.http.get<SystemMetricsDto>('/api/system-metrics'));
  }

  getSystemUpdate(): Promise<SystemUpdateStatusDto> {
    return firstValueFrom(this.http.get<SystemUpdateStatusDto>('/api/system-update'));
  }

  checkSystemUpdate(): Promise<SystemUpdateStatusDto> {
    return firstValueFrom(this.http.post<SystemUpdateStatusDto>('/api/system-update/check', null));
  }

  applySystemUpdate(): Promise<SystemUpdateStatusDto> {
    return firstValueFrom(this.http.post<SystemUpdateStatusDto>('/api/system-update/apply', null));
  }

  async downloadSystemUpdateSupportBundle(): Promise<SystemUpdateSupportBundleDownload> {
    const response = await firstValueFrom(
      this.http.post('/api/system-update/support-bundle', null, {
        observe: 'response',
        responseType: 'blob',
      }),
    );
    return {
      blob: response.body ?? new Blob([], { type: 'application/zip' }),
      fileName: supportBundleFileName(response.headers.get('Content-Disposition')),
    };
  }

  getSourceManagementStatus(): Promise<SourceManagementCapabilityDto> {
    return firstValueFrom(
      this.http.get<SourceManagementCapabilityDto>('/api/source-management/status'),
    );
  }

  addSource(request: SourceAddRequestDto): Promise<SourceManagementOperationDto> {
    return firstValueFrom(
      this.http.post<SourceManagementOperationDto>('/api/source-management/sources', request),
    );
  }

  removeSource(sourceId: string): Promise<SourceManagementOperationDto> {
    return firstValueFrom(
      this.http.delete<SourceManagementOperationDto>(
        `/api/source-management/sources/${encodeURIComponent(sourceId)}`,
      ),
    );
  }

  getSourceManagementOperation(operationId: string): Promise<SourceManagementOperationDto> {
    return firstValueFrom(
      this.http.get<SourceManagementOperationDto>(
        `/api/source-management/operations/${encodeURIComponent(operationId)}`,
      ),
    );
  }

  getSources(): Promise<readonly SourceDto[]> {
    return firstValueFrom(this.http.get<readonly SourceDto[]>('/api/sources'));
  }

  listFiles(sourceId: string, path: string): Promise<readonly FileEntryDto[]> {
    const params = new HttpParams().set('sourceId', sourceId).set('path', path);
    return firstValueFrom(this.http.get<readonly FileEntryDto[]>('/api/files', { params }));
  }

  async listArchive(
    sourceId: string,
    archivePath: string,
    internalPath: string,
  ): Promise<ArchiveDirectoryDto> {
    const params = new HttpParams()
      .set('sourceId', sourceId)
      .set('archivePath', archivePath)
      .set('path', internalPath);
    const response = await firstValueFrom(
      this.http.get<ArchiveDirectoryTransport>('/api/archives/entries', { params }),
    );
    return {
      sourceId: response.sourceId,
      archivePath: response.archivePath,
      path: response.path,
      format: response.format,
      volumeCount: response.volumeCount,
      isReadOnly: true,
      entries: response.entries.map((entry) => ({
        name: entry.name,
        relativePath: entry.path,
        type: entry.type,
        size: entry.size,
        modifiedAt: entry.modifiedAt,
        extension: entry.extension,
        isReadOnly: true,
        isSymbolicLink: false,
        attributes: entry.attributes,
        archiveFormatHint: null,
        archiveRole: null,
      })),
    };
  }

  getInfo(sourceId: string, path: string): Promise<FileEntryDto> {
    const params = new HttpParams().set('sourceId', sourceId).set('path', path);
    return firstValueFrom(this.http.get<FileEntryDto>('/api/files/info', { params }));
  }

  getUploadLimits(): Promise<UploadLimitsDto> {
    return firstValueFrom(this.http.get<UploadLimitsDto>('/api/uploads/limits'));
  }

  uploadFiles(target: UploadTarget, files: readonly File[]): Observable<UploadEvent> {
    const formData = new FormData();
    for (const file of files) {
      formData.append('files', file, file.name);
    }

    const params = new HttpParams()
      .set('sourceId', target.sourceId)
      .set('path', target.directoryPath);
    const request = new HttpRequest('POST', '/api/uploads', formData, {
      params,
      reportProgress: true,
    });

    return this.http.request<UploadResultDto>(request).pipe(
      filter(isUploadTransportEvent),
      map((event): UploadEvent =>
        event.type === HttpEventType.UploadProgress
          ? {
              kind: 'progress',
              loadedBytes: event.loaded,
              totalBytes: event.total ?? null,
            }
          : {
              kind: 'completed',
              result: event.body ?? missingUploadResult(),
            },
      ),
    );
  }

  previewBatchRename(request: BatchRenamePreviewRequestDto): Promise<BatchRenamePreviewDto> {
    return firstValueFrom(
      this.http.post<BatchRenamePreviewDto>('/api/batch-renames/preview', request),
    );
  }

  previewRename(request: ExactRenamePreviewRequestDto): Promise<BatchRenamePreviewDto> {
    return firstValueFrom(this.http.post<BatchRenamePreviewDto>('/api/renames/preview', request));
  }

  executeBatchRename(planId: string): Promise<BatchRenameOperationDto> {
    return firstValueFrom(
      this.http.post<BatchRenameOperationDto>(
        `/api/batch-renames/${encodeURIComponent(planId)}/execute`,
        {},
      ),
    );
  }

  undoBatchRename(operationId: string): Promise<BatchRenameOperationDto> {
    return firstValueFrom(
      this.http.post<BatchRenameOperationDto>(
        `/api/batch-renames/${encodeURIComponent(operationId)}/undo`,
        {},
      ),
    );
  }

  previewArchiveExtraction(
    request: ArchiveExtractionPreviewRequestDto,
  ): Promise<ArchiveExtractionPreviewDto> {
    return firstValueFrom(
      this.http.post<ArchiveExtractionPreviewDto>('/api/archive-extractions/preview', request),
    );
  }

  executeArchiveExtraction(planId: string): Promise<ArchiveExtractionOperationDto> {
    return firstValueFrom(
      this.http.post<ArchiveExtractionOperationDto>(
        `/api/archive-extractions/${encodeURIComponent(planId)}/execute`,
        null,
      ),
    );
  }

  getArchiveExtraction(operationId: string): Promise<ArchiveExtractionOperationDto> {
    return firstValueFrom(
      this.http.get<ArchiveExtractionOperationDto>(
        `/api/archive-extractions/${encodeURIComponent(operationId)}`,
      ),
    );
  }

  cancelArchiveExtraction(operationId: string): Promise<ArchiveExtractionOperationDto> {
    return firstValueFrom(
      this.http.post<ArchiveExtractionOperationDto>(
        `/api/archive-extractions/${encodeURIComponent(operationId)}/cancel`,
        null,
      ),
    );
  }

  previewFileOperation(request: FileOperationPreviewRequestDto): Promise<FileOperationPreviewDto> {
    return firstValueFrom(
      this.http.post<FileOperationPreviewDto>('/api/file-operations/preview', request),
    );
  }

  submitFileOperation(request: FileOperationSubmissionDto): Promise<FileOperationStatusDto> {
    return firstValueFrom(this.http.post<FileOperationStatusDto>('/api/file-operations', request));
  }

  listFileOperations(): Promise<readonly FileOperationStatusDto[]> {
    return firstValueFrom(this.http.get<readonly FileOperationStatusDto[]>('/api/file-operations'));
  }

  getFileOperation(operationId: string): Promise<FileOperationStatusDto> {
    return firstValueFrom(
      this.http.get<FileOperationStatusDto>(
        `/api/file-operations/${encodeURIComponent(operationId)}`,
      ),
    );
  }

  cancelFileOperation(operationId: string): Promise<FileOperationStatusDto> {
    return firstValueFrom(
      this.http.post<FileOperationStatusDto>(
        `/api/file-operations/${encodeURIComponent(operationId)}/cancel`,
        null,
      ),
    );
  }

  acknowledgeFileOperation(operationId: string): Promise<void> {
    return firstValueFrom(
      this.http
        .delete<void>(`/api/file-operations/${encodeURIComponent(operationId)}`)
        .pipe(map(() => undefined)),
    );
  }

  createDirectory(request: CreateDirectoryRequestDto): Promise<FileEntryDto> {
    return firstValueFrom(this.http.post<FileEntryDto>('/api/directories', request));
  }

  previewDelete(request: DeletePreviewRequestDto): Promise<DeletePreviewDto> {
    return firstValueFrom(this.http.post<DeletePreviewDto>('/api/trash/preview-delete', request));
  }

  submitDelete(request: DeleteSubmissionDto): Promise<FileOperationStatusDto> {
    return firstValueFrom(this.http.post<FileOperationStatusDto>('/api/trash/delete', request));
  }

  listTrash(sourceId?: string): Promise<readonly TrashEntryDto[]> {
    const options =
      sourceId === undefined ? {} : { params: new HttpParams().set('sourceId', sourceId) };
    return firstValueFrom(this.http.get<readonly TrashEntryDto[]>('/api/trash', options));
  }

  previewRestore(request: RestorePreviewRequestDto): Promise<RestorePreviewDto> {
    return firstValueFrom(this.http.post<RestorePreviewDto>('/api/trash/preview-restore', request));
  }

  submitRestore(request: RestoreSubmissionDto): Promise<FileOperationStatusDto> {
    return firstValueFrom(this.http.post<FileOperationStatusDto>('/api/trash/restore', request));
  }

  permanentlyDeleteTrash(request: TrashPermanentDeleteRequestDto): Promise<FileOperationStatusDto> {
    return firstValueFrom(
      this.http.delete<FileOperationStatusDto>('/api/trash/items', { body: request }),
    );
  }

  emptyTrash(request: EmptyTrashRequestDto): Promise<FileOperationStatusDto> {
    return firstValueFrom(
      this.http.delete<FileOperationStatusDto>('/api/trash', { body: request }),
    );
  }
}

function isUploadTransportEvent(
  event: HttpEvent<UploadResultDto>,
): event is HttpUploadProgressEvent | HttpResponse<UploadResultDto> {
  return event.type === HttpEventType.UploadProgress || event.type === HttpEventType.Response;
}

function missingUploadResult(): never {
  throw new Error('The upload response did not contain a result.');
}

function supportBundleFileName(contentDisposition: string | null): string {
  const fallback = `reachcommander-support-${new Date()
    .toISOString()
    .replace(/[-:]/g, '')
    .replace(/\.\d{3}Z$/, 'Z')}.zip`;
  const encoded = contentDisposition?.match(/filename\*=UTF-8''([^;]+)/i)?.[1];
  const plain = contentDisposition?.match(/filename="?([^";]+)"?/i)?.[1];
  let candidate = encoded ?? plain ?? '';
  try {
    candidate = decodeURIComponent(candidate);
  } catch {
    return fallback;
  }
  return /^reachcommander-support-\d{8}T\d{6}Z\.zip$/.test(candidate) ? candidate : fallback;
}

interface ArchiveDirectoryTransport {
  readonly sourceId: string;
  readonly archivePath: string;
  readonly path: string;
  readonly format: ArchiveFormat;
  readonly volumeCount: number;
  readonly isReadOnly: true;
  readonly entries: readonly ArchiveEntryTransport[];
}

interface ArchiveEntryTransport {
  readonly path: string;
  readonly name: string;
  readonly type: FileEntryDto['type'];
  readonly size: number | null;
  readonly modifiedAt: string | null;
  readonly extension: string | null;
  readonly attributes: string;
}
