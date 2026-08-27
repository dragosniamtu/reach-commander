import { SystemUpdateStatusDto } from '../../core/api/api.models';
import { buildSystemUpdateTrace } from './system-update-trace';

describe('buildSystemUpdateTrace', () => {
  const now = Date.parse('2026-08-27T10:02:05Z');

  it('shows elapsed time, last host activity, and ordered safe events', () => {
    const view = buildSystemUpdateTrace(statusWithTrace(), now);

    expect(view.elapsedLabel).toBe('2m 5s');
    expect(view.lastActivityLabel).toBe('1s ago');
    expect(view.events.map((event) => event.label)).toEqual([
      'Update accepted',
      'Downloading verified image',
      'Host download activity confirmed',
    ]);
    expect(view.stale).toBe(false);
  });

  it('uses the greater host or local elapsed time only while applying', () => {
    const applying = buildSystemUpdateTrace(statusWithTrace(), now);
    const terminal = buildSystemUpdateTrace(
      statusWithTrace({ phase: 'failed' }),
      Date.parse('2026-08-27T11:00:00Z'),
    );

    expect(applying.elapsedSeconds).toBe(125);
    expect(terminal.elapsedSeconds).toBe(120);
  });

  it('opens details after sixty seconds without a host activity or event', () => {
    const quiet = statusWithTrace({
      trace: {
        ...statusWithTrace().trace!,
        lastActivityAt: null,
        events: statusWithTrace().trace!.events.slice(0, 2),
      },
    });

    expect(buildSystemUpdateTrace(quiet, Date.parse('2026-08-27T10:01:59Z')).stale).toBe(false);
    const stale = buildSystemUpdateTrace(quiet, Date.parse('2026-08-27T10:02:00Z'));
    expect(stale.stale).toBe(true);
    expect(stale.autoOpen).toBe(true);
  });

  it('gives helper-refresh guidance when protocol v3 traces are unavailable', () => {
    const view = buildSystemUpdateTrace(statusWithTrace({ protocolVersion: 2, trace: null }), now);

    expect(view.events).toEqual([]);
    expect(view.guidance).toContain('refresh the Ubuntu installer bundle');
  });

  it('maps a timeout to fixed safe user-facing text', () => {
    const timedOut = statusWithTrace({
      trace: {
        ...statusWithTrace().trace!,
        events: [{
          sequence: 4,
          timestamp: '2026-08-27T10:02:00Z',
          elapsedSeconds: 120,
          code: 'commandTimedOut',
          stage: 'downloading',
          outcome: 'timedOut',
        }],
      },
    });

    const view = buildSystemUpdateTrace(timedOut, now);
    expect(view.events[0]).toEqual(expect.objectContaining({
      label: 'Update command timed out',
      outcomeLabel: 'Timed out',
    }));
    expect(JSON.stringify(view)).not.toMatch(/docker|sha256:|\/opt\/|exitCode|timeoutSeconds/i);
  });
});

function statusWithTrace(
  overrides: Partial<SystemUpdateStatusDto> = {},
): SystemUpdateStatusDto {
  return {
    protocolVersion: 3,
    supported: true,
    channel: 'stable',
    currentVersion: 'v1.3.0',
    targetVersion: 'v1.4.0',
    phase: 'applying',
    progressStage: 'downloading',
    updateAvailable: true,
    canApply: false,
    reasonCode: 'update_applying',
    detail: 'ReachCommander is applying the trusted update.',
    operationId: 'operation-1',
    lastCheckedAt: '2026-08-27T09:59:00Z',
    updatedAt: '2026-08-27T10:02:04Z',
    trace: {
      startedAt: '2026-08-27T10:00:00Z',
      elapsedSeconds: 120,
      lastActivityAt: '2026-08-27T10:02:04Z',
      events: [
        {
          sequence: 1,
          timestamp: '2026-08-27T10:00:00Z',
          elapsedSeconds: 0,
          code: 'operationAccepted',
          stage: null,
          outcome: 'started',
        },
        {
          sequence: 2,
          timestamp: '2026-08-27T10:01:00Z',
          elapsedSeconds: 60,
          code: 'downloadStarted',
          stage: 'downloading',
          outcome: 'started',
        },
        {
          sequence: 3,
          timestamp: '2026-08-27T10:02:04Z',
          elapsedSeconds: 120,
          code: 'hostActivity',
          stage: 'downloading',
          outcome: 'activity',
        },
      ],
    },
    ...overrides,
  };
}
