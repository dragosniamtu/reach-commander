import {
  SystemUpdateStatusDto,
  SystemUpdateTraceEventCode,
  SystemUpdateTraceEventDto,
  SystemUpdateTraceOutcome,
} from '../../core/api/api.models';

export interface SystemUpdateTraceEventView {
  readonly sequence: number;
  readonly label: string;
  readonly elapsedLabel: string;
  readonly outcomeLabel: string;
  readonly outcome: SystemUpdateTraceOutcome;
}

export interface SystemUpdateTraceView {
  readonly available: boolean;
  readonly elapsedSeconds: number;
  readonly elapsedLabel: string;
  readonly lastActivityLabel: string | null;
  readonly events: readonly SystemUpdateTraceEventView[];
  readonly latestEventLabel: string | null;
  readonly stale: boolean;
  readonly autoOpen: boolean;
  readonly guidance: string | null;
}

const staleAfterSeconds = 60;

const labels: Readonly<Record<SystemUpdateTraceEventCode, string>> = {
  operationAccepted: 'Update accepted',
  downloadStarted: 'Downloading verified image',
  hostActivity: 'Host download activity confirmed',
  downloadCompleted: 'Verified image downloaded',
  backupStarted: 'Saving current deployment state',
  backupCompleted: 'Current deployment state saved',
  installStarted: 'Installing updated deployment files',
  installCompleted: 'Updated deployment files installed',
  candidateRestartStarted: 'Restarting ReachCommander',
  candidateRestartCompleted: 'ReachCommander restart finished',
  candidateImageVerified: 'Updated container identity verified',
  candidateHealthStarted: 'Checking updated system health',
  candidateHealthActivity: 'Updated system health activity confirmed',
  candidateHealthSucceeded: 'Updated system is healthy',
  candidateHealthFailed: 'Updated system health check failed',
  rollbackStarted: 'Restoring previous version',
  rollbackStateRestored: 'Previous deployment state restored',
  previousRestartStarted: 'Restarting previous version',
  previousRestartCompleted: 'Previous version restart finished',
  previousImageVerified: 'Previous container identity verified',
  recoveryHealthStarted: 'Checking recovered system health',
  recoveryHealthActivity: 'Recovery health activity confirmed',
  recoveryHealthSucceeded: 'Previous version is healthy',
  recoveryHealthFailed: 'Previous version health check failed',
  commandTimedOut: 'Update command timed out',
  terminationRequested: 'Stopping timed-out update command',
  terminationForced: 'Timed-out update command force-stopped',
  operationCompleted: 'Update completed',
  operationRolledBack: 'Previous version restored',
  operationFailed: 'Update failed',
};

const outcomeLabels: Readonly<Record<SystemUpdateTraceOutcome, string>> = {
  started: 'Started',
  activity: 'Activity confirmed',
  succeeded: 'Completed',
  failed: 'Failed',
  timedOut: 'Timed out',
};

export function buildSystemUpdateTrace(
  status: SystemUpdateStatusDto,
  nowMilliseconds = Date.now(),
): SystemUpdateTraceView {
  const trace = status.trace;
  const terminalAttention = status.phase === 'rolledBack' || status.phase === 'failed';
  if (trace === null) {
    return Object.freeze({
      available: false,
      elapsedSeconds: 0,
      elapsedLabel: '0s',
      lastActivityLabel: null,
      events: Object.freeze([]),
      latestEventLabel: null,
      stale: false,
      autoOpen: terminalAttention,
      guidance: status.protocolVersion < 3
        ? 'Detailed update diagnostics require updater protocol v3; refresh the Ubuntu installer bundle.'
        : 'Waiting for detailed activity from the host updater.',
    });
  }

  const startedAt = parsedMilliseconds(trace.startedAt);
  const locallyObservedElapsed = status.phase === 'applying' && startedAt !== null
    ? Math.max(0, Math.floor((nowMilliseconds - startedAt) / 1_000))
    : 0;
  const elapsedSeconds = Math.max(trace.elapsedSeconds, locallyObservedElapsed);
  const latestEvent = trace.events.length === 0 ? null : trace.events[trace.events.length - 1];
  const activityAt = parsedMilliseconds(trace.lastActivityAt ?? latestEvent?.timestamp ?? trace.startedAt);
  const silentSeconds = activityAt === null
    ? 0
    : Math.max(0, Math.floor((nowMilliseconds - activityAt) / 1_000));
  const stale = status.phase === 'applying' && silentSeconds >= staleAfterSeconds;
  const events = Object.freeze(trace.events.map(eventView));

  return Object.freeze({
    available: true,
    elapsedSeconds,
    elapsedLabel: durationLabel(elapsedSeconds),
    lastActivityLabel: activityAt === null ? null : `${durationLabel(silentSeconds)} ago`,
    events,
    latestEventLabel: events.length === 0 ? null : events[events.length - 1].label,
    stale,
    autoOpen: stale || terminalAttention,
    guidance: null,
  });
}

function eventView(event: SystemUpdateTraceEventDto): SystemUpdateTraceEventView {
  return Object.freeze({
    sequence: event.sequence,
    label: labels[event.code],
    elapsedLabel: `+${durationLabel(event.elapsedSeconds)}`,
    outcomeLabel: outcomeLabels[event.outcome],
    outcome: event.outcome,
  });
}

function parsedMilliseconds(value: string): number | null {
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function durationLabel(totalSeconds: number): string {
  const bounded = Math.max(0, Math.floor(totalSeconds));
  const hours = Math.floor(bounded / 3_600);
  const minutes = Math.floor((bounded % 3_600) / 60);
  const seconds = bounded % 60;
  return [
    hours > 0 ? `${hours}h` : null,
    minutes > 0 ? `${minutes}m` : null,
    `${seconds}s`,
  ].filter((part): part is string => part !== null).join(' ');
}
