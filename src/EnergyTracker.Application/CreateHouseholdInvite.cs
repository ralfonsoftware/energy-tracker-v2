using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>
/// Creates a single-use, expiring HouseholdInvite for the caller's own Household and returns its
/// plaintext token once; only the token's hash is stored (AC #1, Story 11.9 AC #5).
/// Plain class with a constructor-injected repository port, matching CreateHousehold's shape.
/// </summary>
public class CreateHouseholdInvite(IHouseholdRepository repository, TimeProvider timeProvider)
{
    // Fixed operational/security default (not an AD-15 household-scoped product value) — bounds
    // how long a leaked/forwarded invite link stays dangerous.
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);

    public async Task<CreatedHouseholdInvite> ExecuteAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var token = InviteToken.Generate();
        var invite = new HouseholdInvite
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            TokenHash = InviteToken.Hash(token),
            CreatedAtUtc = now,
            ExpiresAtUtc = now + InviteLifetime,
        };

        await repository.AddInviteAsync(invite, cancellationToken);

        return new CreatedHouseholdInvite(token, invite.ExpiresAtUtc);
    }
}
