using Serilog.Core;
using Serilog.Events;

namespace EnergyTracker.Api;

/// <summary>
/// Rewrites invite segments in every string property of every log event, so the hosting request lines keep method,
/// status and timing but show <c>/join/{token}</c>. Scans all properties, not just Path/QueryString, so a renamed one cannot reopen the leak (ADR 0008).
/// </summary>
internal sealed class InviteTokenRedactingEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        // Collect first: the property dictionary cannot change while it is enumerated.
        List<LogEventProperty>? changed = null;
        foreach (var property in logEvent.Properties)
        {
            var redacted = Redact(property.Value);
            if (!ReferenceEquals(redacted, property.Value))
            {
                (changed ??= []).Add(new LogEventProperty(property.Key, redacted));
            }
        }

        if (changed is null)
        {
            return;
        }

        foreach (var property in changed)
        {
            logEvent.AddOrUpdateProperty(property);
        }
    }

    // Returns the same instance when nothing changed, so the caller only rewrites properties that carried an invite segment.
    private static LogEventPropertyValue Redact(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: string text }:
                return RedactScalar(value, text);
            case ScalarValue { Value: Uri uri }:
                return RedactScalar(value, uri.ToString());
            case SequenceValue sequence:
                var items = sequence.Elements.Select(Redact).ToList();
                return items.Zip(sequence.Elements).All(p => ReferenceEquals(p.First, p.Second)) ? value : new SequenceValue(items);
            case StructureValue structure:
                var members = structure.Properties.Select(p => new LogEventProperty(p.Name, Redact(p.Value))).ToList();
                return members.Zip(structure.Properties).All(p => ReferenceEquals(p.First.Value, p.Second.Value))
                    ? value
                    : new StructureValue(members, structure.TypeTag);
            case DictionaryValue dictionary:
                var entries = dictionary.Elements
                    .Select(e => KeyValuePair.Create((ScalarValue)Redact(e.Key), Redact(e.Value)))
                    .ToList();
                return entries.Zip(dictionary.Elements).All(p => ReferenceEquals(p.First.Key, p.Second.Key) && ReferenceEquals(p.First.Value, p.Second.Value))
                    ? value
                    : new DictionaryValue(entries);
            default:
                return value;
        }
    }

    private static LogEventPropertyValue RedactScalar(LogEventPropertyValue value, string text)
    {
        var redacted = InviteTokenRedactor.Redact(text);
        return string.Equals(redacted, text, StringComparison.Ordinal) ? value : new ScalarValue(redacted);
    }
}
