using System.Reflection;

namespace LiveViewEngine.Core.UnitTests;

// Guards the review-driven fix that hid IPositionIndex-only lifecycle operations (subscriber
// refcounting, pending-old-value capture/reset) from SortIndex/NaturalOrderIndex's public surface,
// so they can't be called/relied upon via a concrete reference and become an accidental
// compatibility commitment. Reflection-based so a future contributor re-adding one of these as a
// plain public member gets caught immediately, rather than the regression only being noticed via
// code review.
public class PositionIndexPublicSurfaceTests
{
    private static readonly string[] InternalOnlyMemberNames =
    [
        nameof(IPositionIndex.IncrementSubscribers),
        nameof(IPositionIndex.DecrementSubscribers),
        nameof(IPositionIndex.AffectsOrder),
        nameof(IPositionIndex.CaptureOldValue),
        nameof(IPositionIndex.ResetPending),
        nameof(IPositionIndex.IndexOfWithPendingOldValue),
        nameof(IPositionIndex.WithPendingOldValue)
    ];

    [Theory]
    [InlineData(typeof(SortIndex))]
    [InlineData(typeof(NaturalOrderIndex))]
    public void PositionIndexLifecycleMembers_AreNotPublicOnConcreteType(Type concreteType)
    {
        var publicMemberNames = concreteType
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet();

        foreach (var name in InternalOnlyMemberNames)
        {
            Assert.DoesNotContain(name, publicMemberNames);
        }
    }
}
