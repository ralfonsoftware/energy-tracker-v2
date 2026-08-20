namespace EnergyTracker.Application.Ports;

// AD-14: this port's name, its DTOs, and its implementation file are all outside
// PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests' guarded file list, and may reference
// SmartPlugReading freely — only GetCurrentStatus.cs (which consumes this interface) may not.
// This is the entire mechanism by which Smart Plug data "sharpens" Status (FR-5, AC #2): it can
// only ever soften GetCurrentStatus's existing IsLowConfidence flag, never touch
// PaceToDateKwh/BaselineToDateKwh/the Trending resolution.
public interface ISmartPlugCoverageSignal
{
    Task<bool> HasCoverageDuringAsync(Guid householdId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken);
}
