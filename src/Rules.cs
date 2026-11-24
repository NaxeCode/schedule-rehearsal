using NodaTime;
using NodaTime.Text;
using NodaTime.TimeZones;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rehearsal;

static class Format
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static string Write<T>(T value) => JsonSerializer.Serialize(value, Json);
    internal static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new InvalidDataException("Empty JSON.");
    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static string Utc(DateTimeOffset value) => Instant.FromDateTimeOffset(value).ToString();
    internal static DateTimeOffset Time(string value) => InstantPattern.ExtendedIso.Parse(value).GetValueOrThrow().ToDateTimeOffset();
}

sealed record Manifest(string Id, string Title, string Zone, string HistoryDay, string[] PendingDays,
    string LocalLabel, string LocalTime, string Overlap, string FixedLabel, string FixedUtcTime,
    string PreviousVersion = "2026e", string CandidateVersion = "2026f")
{
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Title) ||
            string.IsNullOrWhiteSpace(FixedLabel) || string.IsNullOrWhiteSpace(LocalLabel))
            throw new InvalidDataException("Give the scenario and both jobs names.");
        if (Overlap is not ("earlier" or "later"))
            throw new InvalidDataException("Choose earlier or later for a repeated wall time.");
        if (PendingDays is not { Length: > 0 and <= 31 } ||
            !PendingDays.SequenceEqual(PendingDays.Distinct().Order(StringComparer.Ordinal)) ||
            string.CompareOrdinal(PendingDays[0], HistoryDay) <= 0)
            throw new InvalidDataException("Supply 1–31 distinct pending days in order after the history day.");
        foreach (var day in PendingDays.Prepend(HistoryDay)) LocalDatePattern.Iso.Parse(day).GetValueOrThrow();
        _ = Rules.ClockTime.Parse(LocalTime).GetValueOrThrow();
        _ = Rules.ClockTime.Parse(FixedUtcTime).GetValueOrThrow();
    }
}

sealed record Occurrence(string Id, string Day, string Intent, string Label, string[] OldMappings,
    string[] NewMappings, string Original, string? Proposed);

static class Rules
{
    internal static readonly LocalTimePattern ClockTime = LocalTimePattern.CreateWithInvariantCulture("HH:mm");
    internal static string Hash(string version) => version switch
    {
        "2026d" => "96c023bfd0be2f902d7e1e1e0e773f29d8b80ea4a28bb4fd7b08f56bcf590144",
        "2026e" => "93bf5daf35dfdbe92a87d660f0e5863d886abe91e9fd0cd4652487b23d20be61",
        "2026f" => "b17c31650e9163761b50645b4ab3806649deb24be55c790857fdd004b5611be3",
        _ => throw new InvalidDataException($"Unknown rule revision {version}.")
    };
    static readonly Dictionary<string, DateTimeZoneCache> Providers = new();
    static DateTimeZoneCache Provider(string version)
    {
        if (!Providers.TryGetValue(version, out var provider))
            Providers.Add(version, provider = Load(version, Hash(version)));
        return provider;
    }

    static DateTimeZoneCache Load(string version, string expected)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", $"tzdb{version}.nzd"));
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expected)
            throw new InvalidDataException($"Wrong hash for {version}.");
        using var stream = new MemoryStream(bytes);
        return new DateTimeZoneCache(TzdbDateTimeZoneSource.FromStream(stream));
    }

    static string[] Map(DateTimeZone zone, LocalDateTime value)
    {
        var mapped = zone.MapLocal(value);
        return mapped.Count switch
        {
            0 => [],
            1 => [mapped.Single().ToInstant().ToString()],
            _ => [mapped.First().ToInstant().ToString(), mapped.Last().ToInstant().ToString()]
        };
    }

    public static string? MoveWallClock(string stored, Manifest manifest)
    {
        var oldWall = InstantPattern.ExtendedIso.Parse(stored).Value.InZone(Provider(manifest.PreviousVersion)[manifest.Zone]).LocalDateTime;
        var options = Map(Provider(manifest.CandidateVersion)[manifest.Zone], oldWall);
        return manifest.Overlap == "later" ? options.LastOrDefault() : options.FirstOrDefault();
    }

    public static Occurrence[] Expand(Manifest manifest)
    {
        manifest.Validate();
        var result = new List<Occurrence>();
        foreach (var day in manifest.PendingDays.Prepend(manifest.HistoryDay))
        {
            var date = LocalDatePattern.Iso.Parse(day).Value;
            var wall = date.At(ClockTime.Parse(manifest.LocalTime).Value);
            var old = Map(Provider(manifest.PreviousVersion)[manifest.Zone], wall);
            var next = Map(Provider(manifest.CandidateVersion)[manifest.Zone], wall);
            string? Choose(string[] options) => manifest.Overlap == "later" ? options.LastOrDefault() : options.FirstOrDefault();
            var original = Choose(old) ?? throw new InvalidDataException($"No old local instant exists at {wall} in {manifest.Zone}.");
            result.Add(new($"local/{day}", day, "local", manifest.LocalLabel, old, next, original, Choose(next)));
            var utc = date.At(ClockTime.Parse(manifest.FixedUtcTime).Value).InUtc().ToInstant().ToString();
            result.Add(new($"fixed/{day}", day, "fixed", manifest.FixedLabel, [utc], [utc], utc, utc));
        }
        var boundary = result.Where(r => r.Day == manifest.HistoryDay).Max(r => Format.Time(r.Original));
        if (result.Any(r => r.Day != manifest.HistoryDay && Format.Time(r.Original) <= boundary))
            throw new InvalidDataException("A pending instant overlaps the history window.");
        return result.ToArray();
    }
}
