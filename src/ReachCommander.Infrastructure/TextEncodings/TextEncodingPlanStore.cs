using ReachCommander.Application.TextEncodings;

namespace ReachCommander.Infrastructure.TextEncodings;

internal sealed record StoredTextEncodingEntry(
    string LogicalPath,
    string PhysicalPath,
    string LogicalDirectory,
    string PhysicalDirectory,
    string FileName,
    TextFileFingerprint Fingerprint,
    TextEncodingKind SourceEncoding,
    TextEncodingKind OutputEncoding,
    TextEncodingPreviewStatus Status);

internal sealed record StoredTextEncodingPlan(
    Guid PlanId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string SourceId,
    IReadOnlyList<StoredTextEncodingEntry> Entries,
    TextEncodingPreview Preview,
    Guid? BoundOperationId);

internal sealed class TextEncodingPlanStore(TimeProvider clock)
{
    private const int MaximumPlans = 128;
    private readonly Dictionary<Guid, StoredTextEncodingPlan> _plans = [];
    private readonly object _gate = new();

    public void Add(StoredTextEncodingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_gate)
        {
            RemoveExpired(clock.GetUtcNow());
            _plans[plan.PlanId] = plan;
            while (_plans.Count > MaximumPlans)
            {
                var oldest = _plans.Values.MinBy(candidate => candidate.CreatedAt);
                if (oldest is null)
                {
                    break;
                }

                _plans.Remove(oldest.PlanId);
            }
        }
    }

    public StoredTextEncodingPlan Get(Guid planId)
    {
        lock (_gate)
        {
            if (!_plans.TryGetValue(planId, out var plan))
            {
                RemoveExpired(clock.GetUtcNow());
                throw TextEncodingException.PlanNotFound();
            }

            var now = clock.GetUtcNow();
            if (plan.ExpiresAt <= now)
            {
                _plans.Remove(planId);
                RemoveExpired(now);
                throw TextEncodingException.PlanExpired();
            }

            RemoveExpired(now, planId);
            return plan;
        }
    }

    public Guid BindOperation(Guid planId, Guid proposedOperationId)
    {
        lock (_gate)
        {
            var plan = Get(planId);
            if (plan.BoundOperationId is { } existingOperationId)
            {
                return existingOperationId;
            }

            _plans[planId] = plan with { BoundOperationId = proposedOperationId };
            return proposedOperationId;
        }
    }

    private void RemoveExpired(DateTimeOffset now, Guid? exceptPlanId = null)
    {
        foreach (var plan in _plans.Values.ToArray())
        {
            if (plan.PlanId != exceptPlanId && plan.ExpiresAt <= now)
            {
                _plans.Remove(plan.PlanId);
            }
        }
    }
}
