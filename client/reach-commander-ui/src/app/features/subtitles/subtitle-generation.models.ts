import { SourceDto } from '../../core/api/api.models';
import { PanelSide, PanelState } from '../../core/state/commander.models';
import { buildVisibleRows } from '../../core/state/file-table.viewmodel';

export interface SubtitleCapabilities {
  readonly enabled: boolean;
  readonly workerAvailable: boolean;
  readonly englishAvailable: boolean;
  readonly romanianAvailable: boolean;
  readonly reasonCode: string | null;
  readonly detail: string | null;
}

export type SubtitleJobPhase =
  | 'queued'
  | 'discovering'
  | 'running'
  | 'cancelling'
  | 'completed'
  | 'cancelled'
  | 'failed'
  | 'interrupted';
export type SubtitleFilePhase =
  'pending' | 'running' | 'completed' | 'skipped' | 'failed' | 'cancelled';

export interface SubtitleJobStatus {
  readonly jobId: string;
  readonly sourceId: string;
  readonly path: string;
  readonly english: boolean;
  readonly romanian: boolean;
  readonly phase: SubtitleJobPhase;
  readonly stage: string | null;
  readonly currentPath: string | null;
  readonly currentPercent: number | null;
  readonly files: readonly {
    readonly path: string;
    readonly phase: SubtitleFilePhase;
    readonly error: string | null;
  }[];
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly error: string | null;
}

export interface SubtitleJobRequest {
  readonly sourceId: string;
  readonly path: string;
  readonly english: boolean;
  readonly romanian: boolean;
  readonly skipExisting: boolean;
}

export interface SubtitleContext {
  readonly panelSide: PanelSide;
  readonly sourceId: string;
  readonly sourceName: string;
  readonly path: string;
}

export function subtitleJobActive(job: SubtitleJobStatus): boolean {
  return ['queued', 'discovering', 'running', 'cancelling'].includes(job.phase);
}

export function captureSubtitleContext(
  panelSide: PanelSide,
  panel: PanelState,
  sources: readonly SourceDto[],
): { context: SubtitleContext | null; error: string | null } {
  const unavailable = (error: string) => ({ context: null, error });
  const tab = panel.tabs.find((candidate) => candidate.id === panel.activeTabId);
  if (!tab || tab.location.kind !== 'filesystem') {
    return unavailable('Subtitles can be generated only in filesystem folders.');
  }
  const source = sources.find((candidate) => candidate.id === tab.location.sourceId);
  if (!source || !source.isAvailable || panel.loading || panel.errorCode) {
    return unavailable('Wait for an available source to finish loading.');
  }
  if (source.isReadOnly) {
    return unavailable(`${source.name} is read-only.`);
  }
  const rows = buildVisibleRows(panel);
  const candidates =
    panel.selectedItems.size > 0
      ? rows.filter((row) => !row.isParent && panel.selectedItems.has(row.relativePath))
      : rows[panel.cursorIndex] && !rows[panel.cursorIndex].isParent
        ? [rows[panel.cursorIndex]]
        : [];
  if (candidates.length !== 1 || panel.selectedItems.size > 1) {
    return unavailable('Select or focus one video or folder.');
  }
  const entry = candidates[0];
  if (entry.isSymbolicLink || entry.isReadOnly) {
    return unavailable('Choose a writable video or folder that is not a symbolic link.');
  }
  if (
    entry.type !== 'directory' &&
    (entry.type !== 'file' || !/\.(mkv|mp4|avi|mov|m4v|webm)$/i.test(entry.name))
  ) {
    return unavailable('Choose a folder or an MKV, MP4, AVI, MOV, M4V, or WebM video.');
  }
  return {
    context: Object.freeze({
      panelSide,
      sourceId: source.id,
      sourceName: source.name,
      path: entry.relativePath,
    }),
    error: null,
  };
}
