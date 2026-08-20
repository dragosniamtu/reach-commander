import { UploadLimitsDto, UploadResultDto, UploadTarget } from '../api/api.models';
import { PanelSide } from './commander.models';

export interface UploadContext extends UploadTarget {
  readonly side: PanelSide;
  readonly sourceName: string;
}

export type UploadPhase =
  'closed' | 'review' | 'uploading' | 'finalizing' | 'completed' | 'failed' | 'cancelled';

export type UploadPreflightCode =
  'upload_empty' | 'upload_file_too_large' | 'upload_batch_too_large' | 'upload_too_many_files';

export interface UploadPreflightIssue {
  readonly code: UploadPreflightCode;
  readonly message: string;
  readonly fileName: string | null;
}

export interface UploadState {
  readonly phase: UploadPhase;
  readonly context: UploadContext | null;
  readonly files: readonly File[];
  readonly limits: UploadLimitsDto | null;
  readonly limitsPending: boolean;
  readonly totalBytes: number;
  readonly preflightIssues: readonly UploadPreflightIssue[];
  readonly progressLoadedBytes: number;
  readonly progressTotalBytes: number | null;
  readonly result: UploadResultDto | null;
  readonly errorCode: string | null;
  readonly errorMessage: string | null;
  readonly requestToken: number;
}
