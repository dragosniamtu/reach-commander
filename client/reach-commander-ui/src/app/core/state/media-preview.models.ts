import {
  MediaPreviewDto,
  SubtitleCueDto,
  SubtitleRepairCapabilitiesDto,
  SubtitleRepairStatusDto,
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
  | 'queued'
  | 'transcoding'
  | 'ready'
  | 'selectingSubtitle'
  | 'planning'
  | 'review'
  | 'saving'
  | 'refreshingSaved'
  | 'savedRefreshFailed'
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
  readonly subtitleSelectionPath: string | null;
  readonly subtitleSelectionFailed: boolean;
  readonly offsetMilliseconds: number;
  readonly timeScale: number;
  readonly repair: SubtitleRepairStatusDto | null;
  readonly repairStatusUnknown: boolean;
  readonly repairCapabilities: SubtitleRepairCapabilitiesDto | null;
  readonly checkingRepairCapabilities: boolean;
  readonly videoTimeMilliseconds: number;
  readonly savePlan: SubtitleSavePlanDto | null;
  readonly saveResult: SubtitleSaveResultDto | null;
  readonly error: MediaPreviewClientError | null;
  readonly requestToken: number;
}
