import {
  MediaPreviewDto,
  SubtitleCueDto,
  SubtitleSavePlanDto,
  SubtitleSaveResultDto,
} from '../api/api.models';

export interface MediaPreviewContext {
  readonly sourceId: string;
  readonly videoPath: string;
  readonly videoName: string;
  readonly sourceReadOnly: boolean;
}

export type MediaPreviewClientPhase =
  | 'closed'
  | 'opening'
  | 'probing'
  | 'transcoding'
  | 'ready'
  | 'selectingSubtitle'
  | 'planning'
  | 'review'
  | 'saving'
  | 'saved'
  | 'failed';

export interface MediaPreviewClientError {
  readonly code: string;
  readonly detail: string;
}

export interface AdjustedSubtitleCue extends SubtitleCueDto {
  readonly startMilliseconds: number;
  readonly endMilliseconds: number;
}

export interface SubtitleCandidate {
  readonly name: string;
  readonly path: string;
}

export interface MediaPreviewState {
  readonly phase: MediaPreviewClientPhase;
  readonly context: MediaPreviewContext | null;
  readonly session: MediaPreviewDto | null;
  readonly subtitleCandidates: readonly SubtitleCandidate[];
  readonly offsetMilliseconds: number;
  readonly videoTimeMilliseconds: number;
  readonly savePlan: SubtitleSavePlanDto | null;
  readonly saveResult: SubtitleSaveResultDto | null;
  readonly error: MediaPreviewClientError | null;
  readonly requestToken: number;
}
