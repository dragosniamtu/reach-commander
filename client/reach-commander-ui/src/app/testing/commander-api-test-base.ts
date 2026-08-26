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
  SystemUpdateStatusDto,
  TrashEntryDto,
  TrashPermanentDeleteRequestDto,
} from '../core/api/api.models';

export abstract class CommanderApiTestBase extends CommanderApiPort {
  override previewRename(
    _request: ExactRenamePreviewRequestDto,
  ): Promise<BatchRenamePreviewDto> {
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
}

function unsupported<T>(): Promise<T> {
  return Promise.reject(new Error('This API method is not used by the current test fake.'));
}
