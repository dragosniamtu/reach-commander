import { SystemUpdateStatusDto } from '../../core/api/api.models';
import { buildSystemUpdateProgress } from './system-update-progress';

describe('buildSystemUpdateProgress', () => {
  const now = Date.parse('2026-08-27T10:00:20Z');

  it('shows connecting before an operation id is assigned', () => {
    const view = buildSystemUpdateProgress(
      status({ phase: 'applying', operationId: null, progressStage: null }),
      false,
      now,
    );

    expect(view.standard.map((step) => [step.label, step.state])).toEqual([
      ['Connecting to update service', 'active'],
      ['Downloading verified image', 'pending'],
      ['Installing update', 'pending'],
      ['Restarting ReachCommander', 'pending'],
      ['Checking system health', 'pending'],
      ['Activating updated application', 'pending'],
    ]);
  });

  it('marks only confirmed detailed stages complete', () => {
    const view = buildSystemUpdateProgress(
      status({
        phase: 'applying',
        operationId: 'operation-1',
        progressStage: 'installing',
      }),
      false,
      now,
    );

    expect(view.standard.map((step) => step.state)).toEqual([
      'complete',
      'complete',
      'active',
      'pending',
      'pending',
      'pending',
    ]);
  });

  it('uses one honest row for protocol-v1 applying status', () => {
    const view = buildSystemUpdateProgress(
      status({ phase: 'applying', operationId: 'operation-1', progressStage: null }),
      false,
      now,
    );

    expect(view.standard).toEqual([
      expect.objectContaining({ label: 'Applying trusted update', state: 'active' }),
    ]);
    expect(view.detailed).toBe(false);
  });

  it('keeps restarting active while the browser reconnects', () => {
    const updating = status({ progressStage: 'restarting' });

    const connected = buildSystemUpdateProgress(updating, false, now);
    const reconnecting = buildSystemUpdateProgress(updating, true, now);

    expect(reconnecting.standard).toEqual(connected.standard);
    expect(reconnecting.currentLabel).toBe('Restarting ReachCommander');
  });

  it('shows health checking only after restart is confirmed', () => {
    const view = buildSystemUpdateProgress(status({ progressStage: 'healthChecking' }), false, now);

    expect(view.standard.map((step) => step.state)).toEqual([
      'complete',
      'complete',
      'complete',
      'complete',
      'active',
      'pending',
    ]);
    expect(view.currentLabel).toBe('Checking system health');
  });

  it.each([
    ['restoring', ['active', 'pending', 'pending']],
    ['restartingPrevious', ['complete', 'active', 'pending']],
    ['verifyingRecovery', ['complete', 'complete', 'active']],
  ] as const)('shows the %s recovery stage', (progressStage, states) => {
    const view = buildSystemUpdateProgress(status({ progressStage }), false, now);

    expect(view.standard.map((step) => step.state)).toEqual([
      'complete',
      'complete',
      'complete',
      'complete',
      'pending',
      'pending',
    ]);
    expect(view.recovery.map((step) => step.state)).toEqual(states);
  });

  it('activates the client-observed application step after completion', () => {
    const view = buildSystemUpdateProgress(
      status({ phase: 'completed', progressStage: 'healthChecking' }),
      false,
      now,
    );

    expect(view.standard.map((step) => step.state)).toEqual([
      'complete',
      'complete',
      'complete',
      'complete',
      'complete',
      'active',
    ]);
    expect(view.currentLabel).toBe('Activating updated application');
  });

  it('marks recovery complete after the previous version is restored', () => {
    const view = buildSystemUpdateProgress(
      status({ phase: 'rolledBack', progressStage: 'verifyingRecovery' }),
      false,
      now,
    );

    expect(view.recovery.map((step) => step.state)).toEqual(['complete', 'complete', 'complete']);
  });

  it('marks the known recovery stage failed when recovery fails', () => {
    const view = buildSystemUpdateProgress(
      status({ phase: 'failed', progressStage: 'restartingPrevious' }),
      false,
      now,
    );

    expect(view.recovery.map((step) => step.state)).toEqual(['complete', 'failed', 'pending']);
  });

  it.each([
    ['2026-08-27T09:59:50Z', true],
    ['2026-08-27T09:59:50.001Z', false],
    ['not-a-date', false],
  ] as const)('derives staleness from a valid applying timestamp %s', (updatedAt, stale) => {
    const view = buildSystemUpdateProgress(status({ updatedAt }), false, now);

    expect(view.stale).toBe(stale);
  });

  it('never marks a terminal result stale', () => {
    const view = buildSystemUpdateProgress(
      status({ phase: 'failed', updatedAt: '2026-08-27T09:00:00Z' }),
      false,
      now,
    );

    expect(view.stale).toBe(false);
  });
});

function status(overrides: Partial<SystemUpdateStatusDto>): SystemUpdateStatusDto {
  return {
    protocolVersion: 1,
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
    updatedAt: '2026-08-27T10:00:00Z',
    trace: null,
    ...overrides,
  };
}
