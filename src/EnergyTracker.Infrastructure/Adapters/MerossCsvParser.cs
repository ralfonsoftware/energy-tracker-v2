using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Infrastructure.Adapters;

// AD-9 vendor adapter. Real byte layout verified directly against sample-data/meross/*.csv during
// story creation: UTF-8 with BOM, field delimiter is literally "\t," (tab then comma), every line
// has a trailing tab before the newline. Device/Power Point identity comes from the filename
// (AC #4) — the file body carries no device/room identifier at all.
public partial class MerossCsvParser : ISmartPlugParser
{
    public SmartPlugVendorFormat Vendor => SmartPlugVendorFormat.Meross;

    public bool CanParse(string fileName) => FileNamePattern().IsMatch(fileName);

    public IReadOnlyList<SmartPlugReading> Parse(Stream fileContent, string fileName, CancellationToken cancellationToken = default)
    {
        var match = FileNamePattern().Match(fileName);
        if (!match.Success)
        {
            throw new InvalidOperationException($"File name '{fileName}' does not match the Meross export pattern.");
        }

        var deviceTag = match.Groups["device"].Value;

        using var reader = new StreamReader(fileContent, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        reader.ReadLine(); // Header: "Date\t,Power Consumption-(kWh)\t"

        var readings = new List<SmartPlugReading>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = line.Split("\t,");
            if (fields.Length < 2)
            {
                // Malformed/truncated row — skip it rather than aborting the whole file on one
                // bad line via an unhandled IndexOutOfRangeException.
                continue;
            }

            var date = DateOnly.ParseExact(fields[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var kwh = decimal.Parse(fields[1].Trim(), CultureInfo.InvariantCulture);

            // One row per day (filename says "Day Data") — coarser granularity than Eve Home's
            // 10-minute intervals; the interval spans the full day. Same "local time, never
            // UTC-converted" discipline as EveHomeXlsxParser (AD-9).
            var dayStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var dayEnd = dayStart.AddDays(1).AddTicks(-1);

            readings.Add(new SmartPlugReading
            {
                Id = Guid.NewGuid(),
                HouseholdId = Guid.Empty,
                SmartPlugImportId = Guid.Empty,
                PowerPointId = null,
                RoomName = string.Empty,
                PowerPointName = deviceTag,
                DeviceName = deviceTag,
                IntervalStart = dayStart,
                IntervalEnd = dayEnd,
                KwhValue = kwh,
            });
        }

        return readings;
    }

    [GeneratedRegex(@"^Power Monitor Day Data - (?<device>.+) - \d{8}\.csv$", RegexOptions.IgnoreCase)]
    private static partial Regex FileNamePattern();
}
