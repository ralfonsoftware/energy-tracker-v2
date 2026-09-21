using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Creates an Event for the caller's own Household, optionally tagged to a live, non-archived Room/PowerPoint/Device (AC #1, #2, #3).</summary>
public class CreateEvent(IEventRepository repository, ITaggingScaffoldRepository taggingScaffoldRepository, IBackgroundJobQueue jobQueue)
{
    private const int MaxDescriptionLength = 500;

    // Same clock-skew allowance and reasoning as CreateMeterReading — a client's local clock can
    // legitimately be a few minutes off from the server's.
    private static readonly TimeSpan MaxFutureClockSkew = TimeSpan.FromMinutes(5);

    // Same floor and reasoning as CreateMeterReading — a generous floor that only exists to catch
    // an obviously-wrong client-side date-parsing bug, not "no Events existed before X".
    private static readonly DateTimeOffset MinOccurredAt = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task<Event> ExecuteAsync(
        Guid householdId,
        string? description,
        DateTimeOffset occurredAt,
        string? taggedEntityType,
        Guid? taggedEntityId,
        CancellationToken cancellationToken)
    {
        // Nullable parameter + IsNullOrWhiteSpace, matching TaggingScaffoldNameValidator.Validate's
        // precedent: JSON binding happily supplies null for a non-nullable ref-type property (STJ's
        // RespectNullableAnnotations is opt-in and off here), so dereferencing first would surface a
        // NullReferenceException as a 500 instead of this 400.
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new EventValidationException("Description must not be blank.", "event.description_blank");
        }

        var trimmedDescription = description.Trim();
        if (trimmedDescription.Length > MaxDescriptionLength)
        {
            throw new EventValidationException(
                $"Description must be at most {MaxDescriptionLength} characters.", "event.description_too_long");
        }

        var latestAllowedTimestamp = DateTimeOffset.UtcNow.Add(MaxFutureClockSkew);
        if (occurredAt > latestAllowedTimestamp)
        {
            throw new EventValidationException(
                $"Event timestamp '{occurredAt}' is too far in the future.", "event.timestamp_future");
        }

        if (occurredAt < MinOccurredAt)
        {
            throw new EventValidationException(
                $"Event timestamp '{occurredAt}' is unreasonably far in the past.", "event.timestamp_too_old");
        }

        if (taggedEntityType is null != taggedEntityId is null)
        {
            throw new EventValidationException(
                "TaggedEntityType and TaggedEntityId must both be present or both be absent.", "event.tag_pair_mismatch");
        }

        string? taggedEntityName = null;
        if (taggedEntityType is not null && taggedEntityId is { } id)
        {
            taggedEntityName = await ResolveTaggedEntityNameAsync(taggedEntityType, id, cancellationToken);
        }

        var @event = new Event
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Description = trimmedDescription,
            OccurredAt = occurredAt,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            TaggedEntityType = taggedEntityType,
            TaggedEntityId = taggedEntityId,
            TaggedEntityName = taggedEntityName,
        };

        var persisted = await repository.AddAsync(@event, cancellationToken);

        // Story 6.3 (AC #6, #7): unconditional — the AiPlausibilityEnabled/backend-configured check
        // lives entirely inside CorrelateEvent (AD-8's anti-hard-branch rule), never here.
        await jobQueue.EnqueueAsync(
            new JobEnvelope<CorrelateEventPayload>(
                Guid.NewGuid(), householdId, JobTypes.CorrelateEvent,
                new CorrelateEventPayload(persisted.Id, householdId, persisted.OccurredAt, persisted.Description)),
            cancellationToken);

        return persisted;
    }

    // Resolves via the existing ITaggingScaffoldRepository — one port for the whole Room/
    // PowerPoint/Device scaffold, not three separate repositories. Cross-Household access is
    // already impossible here — Find*Async relies on AD-3's DbContext query filter, which scopes
    // every read to the caller's own Household automatically, so a foreign id simply reads as
    // missing and takes the not-found path below without disclosing that it exists elsewhere.
    private async Task<string> ResolveTaggedEntityNameAsync(string taggedEntityType, Guid taggedEntityId, CancellationToken cancellationToken)
    {
        switch (taggedEntityType)
        {
            case "Room":
            {
                var room = await taggingScaffoldRepository.FindRoomAsync(taggedEntityId, cancellationToken);
                if (room is null)
                {
                    throw new EventTaggedEntityNotFoundException(taggedEntityType, taggedEntityId);
                }

                if (room.ArchivedAt is not null)
                {
                    throw new EventTaggedEntityArchivedException(taggedEntityType, taggedEntityId);
                }

                return room.Name;
            }
            case "PowerPoint":
            {
                var powerPoint = await taggingScaffoldRepository.FindPowerPointAsync(taggedEntityId, cancellationToken);
                if (powerPoint is null)
                {
                    throw new EventTaggedEntityNotFoundException(taggedEntityType, taggedEntityId);
                }

                if (powerPoint.ArchivedAt is not null)
                {
                    throw new EventTaggedEntityArchivedException(taggedEntityType, taggedEntityId);
                }

                // ArchiveRoom deliberately does not cascade to its Power Points, so a live Power
                // Point can still sit under an archived Room — a hierarchy the household has
                // already retired, and not a valid new tag target.
                await EnsureAncestorsAreLiveAsync(taggedEntityType, taggedEntityId, powerPoint.RoomId, cancellationToken);

                return powerPoint.Name;
            }
            case "Device":
            {
                var device = await taggingScaffoldRepository.FindDeviceAsync(taggedEntityId, cancellationToken);
                if (device is null)
                {
                    throw new EventTaggedEntityNotFoundException(taggedEntityType, taggedEntityId);
                }

                if (device.ArchivedAt is not null)
                {
                    throw new EventTaggedEntityArchivedException(taggedEntityType, taggedEntityId);
                }

                var parentPowerPoint = await taggingScaffoldRepository.FindPowerPointAsync(device.PowerPointId, cancellationToken);
                if (parentPowerPoint is null || parentPowerPoint.ArchivedAt is not null)
                {
                    throw new EventTaggedEntityArchivedException(taggedEntityType, taggedEntityId, viaAncestor: true);
                }

                await EnsureAncestorsAreLiveAsync(taggedEntityType, taggedEntityId, parentPowerPoint.RoomId, cancellationToken);

                return device.Name;
            }
            default:
                throw new EventValidationException(
                    $"TaggedEntityType '{taggedEntityType}' is not a recognized taggable entity type.", "event.tag_type_unknown");
        }
    }

    private async Task EnsureAncestorsAreLiveAsync(string taggedEntityType, Guid taggedEntityId, Guid roomId, CancellationToken cancellationToken)
    {
        var room = await taggingScaffoldRepository.FindRoomAsync(roomId, cancellationToken);
        if (room is null || room.ArchivedAt is not null)
        {
            throw new EventTaggedEntityArchivedException(taggedEntityType, taggedEntityId, viaAncestor: true);
        }
    }
}
