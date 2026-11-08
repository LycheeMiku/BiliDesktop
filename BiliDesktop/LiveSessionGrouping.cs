namespace BiliDesktop;

public static class LiveSessionGrouping
{
    public static IReadOnlyList<Guid> RelatedSessions(IReadOnlyList<LiveEvent> events, int room, Guid lastSession, TimeSpan maximumGap)
    {
        if (lastSession == Guid.Empty) return [];
        var relevant = events.Where(e => e.RoomId == room).ToArray();
        var sessions = relevant.Where(e => e.Kind == "started").GroupBy(e => e.SessionId)
            .Select(g => new { Id = g.Key, Start = g.Min(x => x.Time), End = relevant.Where(x => x.Kind == "ended" && x.SessionId == g.Key).Select(x => (DateTimeOffset?)x.Time).Max() })
            .Where(x => x.End.HasValue).OrderBy(x => x.Start).ToArray();
        var index = Array.FindIndex(sessions, x => x.Id == lastSession);
        if (index < 0) return [lastSession];
        var selected = new HashSet<Guid> { sessions[index].Id };
        while (index > 0)
        {
            var gap = sessions[index].Start - sessions[index - 1].End!.Value;
            if (gap < TimeSpan.FromSeconds(-10) || gap > maximumGap) break;
            selected.Add(sessions[--index].Id);
        }
        return sessions.Where(x => selected.Contains(x.Id)).Select(x => x.Id).ToArray();
    }

    public static IReadOnlyList<string> RelatedFiles(IReadOnlyList<LiveEvent> events, int room, Guid lastSession, TimeSpan maximumGap)
    {
        var selected = RelatedSessions(events, room, lastSession, maximumGap).ToHashSet();
        return events.Where(e => e.RoomId == room && e.Kind == "file" && selected.Contains(e.SessionId) && !string.IsNullOrWhiteSpace(e.Path)).OrderBy(e => e.Time).Select(e => e.Path!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
