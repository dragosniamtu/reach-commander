import { SystemUpdateProgressStage, SystemUpdateStatusDto } from '../../core/api/api.models';

export type UpdateProgressStepState = 'complete' | 'active' | 'pending' | 'failed';

export interface UpdateProgressStep {
  readonly id: string;
  readonly label: string;
  readonly state: UpdateProgressStepState;
}

export interface SystemUpdateProgressView {
  readonly standard: readonly UpdateProgressStep[];
  readonly recovery: readonly UpdateProgressStep[];
  readonly currentLabel: string | null;
  readonly stale: boolean;
  readonly detailed: boolean;
}

interface StepDefinition {
  readonly id: string;
  readonly label: string;
}

const standardDefinitions: readonly StepDefinition[] = [
  { id: 'connecting', label: 'Connecting to update service' },
  { id: 'downloading', label: 'Downloading verified image' },
  { id: 'installing', label: 'Installing update' },
  { id: 'restarting', label: 'Restarting ReachCommander' },
  { id: 'health-checking', label: 'Checking system health' },
  { id: 'activating', label: 'Activating updated application' },
];

const recoveryDefinitions: readonly StepDefinition[] = [
  { id: 'restoring', label: 'Restoring previous version' },
  { id: 'restarting-previous', label: 'Restarting previous version' },
  { id: 'verifying-recovery', label: 'Verifying recovery' },
];

const healthyStageIndex: Readonly<Partial<Record<SystemUpdateProgressStage, number>>> = {
  downloading: 1,
  installing: 2,
  restarting: 3,
  healthChecking: 4,
};

const recoveryStageIndex: Readonly<Partial<Record<SystemUpdateProgressStage, number>>> = {
  restoring: 0,
  restartingPrevious: 1,
  verifyingRecovery: 2,
};

export function buildSystemUpdateProgress(
  status: SystemUpdateStatusDto,
  _reconnecting: boolean,
  nowMilliseconds = Date.now(),
): SystemUpdateProgressView {
  const stale = isStale(status, nowMilliseconds);

  if (status.phase === 'applying' && status.operationId === null) {
    return view(
      statesForActive(standardDefinitions, 0),
      [],
      standardDefinitions[0].label,
      stale,
      true,
    );
  }

  if (status.phase === 'completed') {
    return view(
      statesForActive(standardDefinitions, 5),
      [],
      standardDefinitions[5].label,
      false,
      true,
    );
  }

  if (status.phase === 'rolledBack') {
    return view(
      recoveryStandardSteps(),
      steps(recoveryDefinitions, ['complete', 'complete', 'complete']),
      null,
      false,
      true,
    );
  }

  const recoveryIndex =
    status.progressStage === null ? undefined : recoveryStageIndex[status.progressStage];
  if (recoveryIndex !== undefined) {
    const recovery =
      status.phase === 'failed'
        ? statesForFailed(recoveryDefinitions, recoveryIndex)
        : statesForActive(recoveryDefinitions, recoveryIndex);
    return view(
      recoveryStandardSteps(),
      recovery,
      recoveryDefinitions[recoveryIndex].label,
      stale,
      true,
    );
  }

  const healthyIndex =
    status.progressStage === null ? undefined : healthyStageIndex[status.progressStage];
  if (healthyIndex !== undefined) {
    const standard =
      status.phase === 'failed'
        ? statesForFailed(standardDefinitions, healthyIndex)
        : statesForActive(standardDefinitions, healthyIndex);
    return view(standard, [], standardDefinitions[healthyIndex].label, stale, true);
  }

  const genericState: UpdateProgressStepState = status.phase === 'failed' ? 'failed' : 'active';
  const generic = Object.freeze({
    id: 'applying',
    label: 'Applying trusted update',
    state: genericState,
  });
  return view([generic], [], generic.label, stale, false);
}

function recoveryStandardSteps(): readonly UpdateProgressStep[] {
  return steps(standardDefinitions, [
    'complete',
    'complete',
    'complete',
    'complete',
    'pending',
    'pending',
  ]);
}

function statesForActive(
  definitions: readonly StepDefinition[],
  activeIndex: number,
): readonly UpdateProgressStep[] {
  return steps(
    definitions,
    definitions.map((_, index) =>
      index < activeIndex ? 'complete' : index === activeIndex ? 'active' : 'pending',
    ),
  );
}

function statesForFailed(
  definitions: readonly StepDefinition[],
  failedIndex: number,
): readonly UpdateProgressStep[] {
  return steps(
    definitions,
    definitions.map((_, index) =>
      index < failedIndex ? 'complete' : index === failedIndex ? 'failed' : 'pending',
    ),
  );
}

function steps(
  definitions: readonly StepDefinition[],
  states: readonly UpdateProgressStepState[],
): readonly UpdateProgressStep[] {
  return Object.freeze(
    definitions.map((definition, index) =>
      Object.freeze({
        ...definition,
        state: states[index],
      }),
    ),
  );
}

function view(
  standard: readonly UpdateProgressStep[],
  recovery: readonly UpdateProgressStep[],
  currentLabel: string | null,
  stale: boolean,
  detailed: boolean,
): SystemUpdateProgressView {
  return Object.freeze({ standard, recovery, currentLabel, stale, detailed });
}

function isStale(status: SystemUpdateStatusDto, nowMilliseconds: number): boolean {
  if (status.phase !== 'applying' || status.operationId === null) {
    return false;
  }

  const updatedAt = Date.parse(status.updatedAt);
  return Number.isFinite(updatedAt) && nowMilliseconds - updatedAt >= 30_000;
}
