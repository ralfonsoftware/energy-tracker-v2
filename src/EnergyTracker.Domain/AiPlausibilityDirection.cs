namespace EnergyTracker.Domain;

// AD-8/FR-17: the AI classification call is deliberately binary-or-nothing — never a confidence
// score or free-text explanation (UX-DR17's no-false-precision rule). A third "no deviation" case
// is represented by a null AiPlausibilityDirection? result, never a member of this enum, mirroring
// Status's "undefined is a null result, not a 4th case" convention.
public enum AiPlausibilityDirection
{
    Bump,
    Dip,
}
