using Microsoft.Extensions.Options;
using ReachCommander.Application.MediaPreviews;

namespace ReachCommander.Infrastructure.MediaPreviews;

internal sealed record StoredSubtitleSavePlan(
    Guid PlanId,
    Guid SessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string SourceId,
    string DirectoryLogicalPath,
    string DirectoryPhysicalPath,
    string SubtitleLogicalPath,
    string SubtitlePhysicalPath,
    string BackupLogicalPath,
    string BackupPhysicalPath,
    MediaFileFingerprint OriginalFingerprint,
    byte[] CorrectedBytes,
    long OffsetMilliseconds);

internal sealed class SubtitleSavePlanStore(
    TimeProvider clock,
    IOptions<MediaPreviewOptions> options)
{
    private const int MaximumPlans = 128;
    private readonly Dictionary<Guid, StoredSubtitleSavePlan> _plans = new();
    private readonly object _gate = new();
    private readonly TimeSpan _lifetime = options.Value.SavePlanLifetime;

    public DateTimeOffset ExpiresAt(DateTimeOffset createdAt) => createdAt + _lifetime;

    public void Add(StoredSubtitleSavePlan plan)
    {
        lock (_gate)
        {
            RemoveExpired(clock.GetUtcNow());
            if (_plans.ContainsKey(plan.PlanId))
            {
                throw new InvalidOperationException("A subtitle save plan ID was reused.");
            }

            if (_plans.Count >= MaximumPlans)
            {
                var oldest = _plans.Values.MinBy(existing => existing.CreatedAt);
                if (oldest is not null)
                {
                    _plans.Remove(oldest.PlanId);
                }
            }

            _plans.Add(plan.PlanId, plan);
        }
    }

    public StoredSubtitleSavePlan GetRequired(Guid planId)
    {
        lock (_gate)
        {
            if (!_plans.TryGetValue(planId, out var plan))
            {
                throw MediaPreviewException.SubtitleSavePlanNotFound();
            }

            if (plan.ExpiresAt <= clock.GetUtcNow())
            {
                _plans.Remove(planId);
                throw MediaPreviewException.SubtitleSavePlanExpired();
            }

            return plan;
        }
    }

    public void Remove(Guid planId)
    {
        lock (_gate)
        {
            _plans.Remove(planId);
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var plan in _plans.Values.Where(plan => plan.ExpiresAt <= now).ToArray())
        {
            _plans.Remove(plan.PlanId);
        }
    }
}
