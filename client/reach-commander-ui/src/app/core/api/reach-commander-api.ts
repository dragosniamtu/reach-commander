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
  BatchRenameOperationDto,
  BatchRenamePreviewDto,
  BatchRenamePreviewRequestDto,
  FileEntryDto,
  SourceDto,
  SystemMetricsDto,
  UploadEvent,
  UploadLimitsDto,
  UploadResultDto,
  UploadTarget,
} from './api.models';

@Injectable({ providedIn: 'root' })
export class ReachCommanderApi extends CommanderApiPort {
  constructor(private readonly http: HttpClient) {
    super();
  }

  getSystemMetrics(): Promise<SystemMetricsDto> {
    return firstValueFrom(this.http.get<SystemMetricsDto>('/api/system-metrics'));
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
}

function isUploadTransportEvent(
  event: HttpEvent<UploadResultDto>,
): event is HttpUploadProgressEvent | HttpResponse<UploadResultDto> {
  return event.type === HttpEventType.UploadProgress || event.type === HttpEventType.Response;
}

function missingUploadResult(): never {
  throw new Error('The upload response did not contain a result.');
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
