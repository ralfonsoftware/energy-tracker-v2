using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>
/// Creates a single-use, expiring HouseholdInvite for the caller's own Household (AC #1).
/// Plain class with a constructor-injected repository port, matching CreateHousehold's shape.
/// </summary>
public class CreateHouseholdInvite(IHouseholdRepository repository)
{
    // Fixed operational/security default (not an AD-15 household-scoped product value) — bounds
    // how long a leaked/forwarded invite link stays dangerous.
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);

    public async Task<HouseholdInvite> ExecuteAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var invite = new HouseholdInvite
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Token = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = now,
            ExpiresAtUtc = now + InviteLifetime,
        };

        await repository.AddInviteAsync(invite, cancellationToken);

        return invite;
    }
}
