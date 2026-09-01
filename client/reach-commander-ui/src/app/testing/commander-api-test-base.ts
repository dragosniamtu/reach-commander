import {
  CommanderApiPort,
  CreateDirectoryRequestDto,
  DeletePreviewDto,
  DeletePreviewRequestDto,
  DeleteSubmissionDto,
  EmptyTrashRequestDto,
  ExactRenamePreviewRequestDto,
  BatchRenamePreviewDto,
  FileEntryDto,
  FileOperationPreviewDto,
  FileOperationPreviewRequestDto,
  FileOperationStatusDto,
  FileOperationSubmissionDto,
  RestorePreviewDto,
  RestorePreviewRequestDto,
  RestoreSubmissionDto,
  SourceAddRequestDto,
  SourceManagementCapabilityDto,
  SourceManagementOperationDto,
  SystemUpdateStatusDto,
  SystemUpdateSupportBundleDownload,
  TrashEntryDto,
  TrashPermanentDeleteRequestDto,
  CreateMediaPreviewRequestDto,
  MediaPreviewDto,
  SubtitleSavePlanDto,
  SubtitleSaveResultDto,
} from '../core/api/api.models';

export abstract class CommanderApiTestBase extends CommanderApiPort {
  override getSourceManagementStatus(): Promise<SourceManagementCapabilityDto> {
    return unsupported();
  }

  override addSource(_request: SourceAddRequestDto): Promise<SourceManagementOperationDto> {
    return unsupported();
  }

  override removeSource(_sourceId: string): Promise<SourceManagementOperationDto> {
    return unsupported();
  }

  override getSourceManagementOperation(
    _operationId: string,
  ): Promise<SourceManagementOperationDto> {
    return unsupported();
  }

  override previewRename(_request: ExactRenamePreviewRequestDto): Promise<BatchRenamePreviewDto> {
    return unsupported();
  }

  override getSystemUpdate(): Promise<SystemUpdateStatusDto> {
    return unsupported();
  }

  override checkSystemUpdate(): Promise<SystemUpdateStatusDto> {
    return unsupported();
  }

  override applySystemUpdate(): Promise<SystemUpdateStatusDto> {
    return unsupported();
  }

  override downloadSystemUpdateSupportBundle(): Promise<SystemUpdateSupportBundleDownload> {
    return unsupported();
  }

  override previewFileOperation(
    _request: FileOperationPreviewRequestDto,
  ): Promise<FileOperationPreviewDto> {
    return unsupported();
  }

  override submitFileOperation(
    _request: FileOperationSubmissionDto,
  ): Promise<FileOperationStatusDto> {
    return unsupported();
  }

  override listFileOperations(): Promise<readonly FileOperationStatusDto[]> {
    return unsupported();
  }

  override getFileOperation(_operationId: string): Promise<FileOperationStatusDto> {
    return unsupported();
  }

  override cancelFileOperation(_operationId: string): Promise<FileOperationStatusDto> {
    return unsupported();
  }

  override acknowledgeFileOperation(_operationId: string): Promise<void> {
    return unsupported();
  }

  override createDirectory(_request: CreateDirectoryRequestDto): Promise<FileEntryDto> {
    return unsupported();
  }

  override previewDelete(_request: DeletePreviewRequestDto): Promise<DeletePreviewDto> {
    return unsupported();
  }

  override submitDelete(_request: DeleteSubmissionDto): Promise<FileOperationStatusDto> {
    return unsupported();
  }

  override listTrash(_sourceId?: string): Promise<readonly TrashEntryDto[]> {
    return unsupported();
  }

  override previewRestore(_request: RestorePreviewRequestDto): Promise<RestorePreviewDto> {
    return unsupported();
  }

  override submitRestore(_request: RestoreSubmissionDto): Promise<FileOperationStatusDto> {
    return unsupported();
  }

  override permanentlyDeleteTrash(
    _request: TrashPermanentDeleteRequestDto,
  ): Promise<FileOperationStatusDto> {
    return unsupported();
  }

  override emptyTrash(_request: EmptyTrashRequestDto): Promise<FileOperationStatusDto> {
    return unsupported();
  }

  override createMediaPreview(_request: CreateMediaPreviewRequestDto): Promise<MediaPreviewDto> {
    return unsupported();
  }

  override getMediaPreview(_sessionId: string): Promise<MediaPreviewDto> {
    return unsupported();
  }

  override selectMediaPreviewSubtitle(
    _sessionId: string,
    _subtitlePath: string,
  ): Promise<MediaPreviewDto> {
    return unsupported();
  }

  override requestMediaPreviewFallback(_sessionId: string): Promise<MediaPreviewDto> {
    return unsupported();
  }

  override planMediaPreviewSubtitleSave(
    _sessionId: string,
    _offsetMilliseconds: number,
  ): Promise<SubtitleSavePlanDto> {
    return unsupported();
  }

  override executeMediaPreviewSubtitleSave(_planId: string): Promise<SubtitleSaveResultDto> {
    return unsupported();
  }

  override closeMediaPreview(_sessionId: string): Promise<void> {
    return unsupported();
  }

  override mediaPreviewContentUrl(sessionId: string): string {
    return `/api/media-previews/${encodeURIComponent(sessionId)}/content`;
  }

  override mediaPreviewHlsUrl(sessionId: string, assetName: string): string {
    return `/api/media-previews/${encodeURIComponent(sessionId)}/hls/${encodeURIComponent(assetName)}`;
  }
}

function unsupported<T>(): Promise<T> {
  return Promise.reject(new Error('This API method is not used by the current test fake.'));
}
