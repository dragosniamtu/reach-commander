import { ArchiveFormat, SourceDto } from '../api/api.models';
import { buildVisibleRows } from './file-table.viewmodel';
import { PanelSide, PanelState } from './commander.models';

export interface ArchiveExtractionContext {
  readonly sourcePanelSide: PanelSide;
  readonly destinationPanelSide: PanelSide;
  readonly sourceId: string;
  readonly sourceName: string;
  readonly archivePath: string;
  readonly internalDirectory: string;
  readonly entryPaths: readonly string[];
  readonly extractAll: boolean;
  readonly format: ArchiveFormat;
  readonly volumeCount: number;
  readonly destinationSourceId: string;
  readonly destinationSourceName: string;
  readonly destinationPath: string;
}

export interface ArchiveExtractionContextResult {
  readonly context: ArchiveExtractionContext | null;
  readonly error: string | null;
}

export type ArchiveExtractionPhase =
  'closed' | 'previewing' | 'review' | 'starting' | 'running' | 'cancelling' |
  'completed' | 'cancelled' | 'failed' | 'recoveryRequired';

export interface ArchiveExtractionSafeError {
  readonly code: string;
  readonly detail: string;
}

export function captureArchiveExtractionContext(
  activeSide: PanelSide,
  activePanel: PanelState,
  oppositePanel: PanelState,
  sources: readonly SourceDto[],
): ArchiveExtractionContextResult {
  const sourceTab = activePanel.tabs.find((tab) => tab.id === activePanel.activeTabId);
  const destinationTab = oppositePanel.tabs.find((tab) => tab.id === oppositePanel.activeTabId);
  if (!sourceTab) {
    return failure('The active panel does not contain an extraction source.');
  }

  const source = sources.find((candidate) => candidate.id === sourceTab.location.sourceId);
  if (!source || !source.isAvailable) {
    return failure(source ? `${source.name} is unavailable.` : 'The active source is unavailable.');
  }

  if (!destinationTab) {
    return failure('The opposite panel does not have a destination folder.');
  }
  if (destinationTab.location.kind !== 'filesystem') {
    return failure('Choose a filesystem folder in the opposite panel.');
  }

  const destination = sources.find(
    (candidate) => candidate.id === destinationTab.location.sourceId,
  );
  if (!destination) {
    return failure('The opposite panel does not have a destination folder.');
  }
  if (!destination.isAvailable) {
    return failure(`${destination.name} is unavailable.`);
  }
  if (destination.isReadOnly) {
    return failure(`${destination.name} is read-only.`);
  }

  const rows = buildVisibleRows(activePanel);
  const candidates = activePanel.selectedItems.size > 0
    ? rows.filter((row) => !row.isParent && activePanel.selectedItems.has(row.relativePath))
    : rows[activePanel.cursorIndex] && !rows[activePanel.cursorIndex]!.isParent
      ? [rows[activePanel.cursorIndex]!]
      : [];
  if (sourceTab.location.kind === 'archive') {
    if (candidates.length === 0) {
      return failure('Select or focus an archive entry to extract.');
    }
    if (!activePanel.archiveMetadata) {
      return failure('Archive metadata is not available.');
    }

    return success({
      sourcePanelSide: activeSide,
      destinationPanelSide: otherSide(activeSide),
      sourceId: source.id,
      sourceName: source.name,
      archivePath: sourceTab.location.archivePath,
      internalDirectory: sourceTab.location.internalPath,
      entryPaths: candidates.map((entry) => entry.relativePath),
      extractAll: false,
      format: activePanel.archiveMetadata.format,
      volumeCount: activePanel.archiveMetadata.volumeCount,
      destinationSourceId: destination.id,
      destinationSourceName: destination.name,
      destinationPath: destinationTab.location.path,
    });
  }

  if (candidates.length !== 1) {
    return failure('Select exactly one archive to extract.');
  }
  const candidate = candidates[0]!;
  if (!candidate.archiveFormatHint || !candidate.archiveRole) {
    return failure('Select a supported archive to extract.');
  }
  if (candidate.archiveRole === 'secondary') {
    return failure('Open the primary archive volume before extracting.');
  }

  return success({
    sourcePanelSide: activeSide,
    destinationPanelSide: otherSide(activeSide),
    sourceId: source.id,
    sourceName: source.name,
    archivePath: candidate.relativePath,
    internalDirectory: '/',
    entryPaths: [],
    extractAll: true,
    format: candidate.archiveFormatHint,
    volumeCount: 1,
    destinationSourceId: destination.id,
    destinationSourceName: destination.name,
    destinationPath: destinationTab.location.path,
  });
}

function success(context: ArchiveExtractionContext): ArchiveExtractionContextResult {
  return {
    context: Object.freeze({ ...context, entryPaths: Object.freeze([...context.entryPaths]) }),
    error: null,
  };
}

function failure(error: string): ArchiveExtractionContextResult {
  return { context: null, error };
}

function otherSide(side: PanelSide): PanelSide {
  return side === 'left' ? 'right' : 'left';
}
