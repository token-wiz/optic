internal record LockRow(
    string Source,
    decimal AmountOpt,
    DateTimeOffset? StartTime,
    DateTimeOffset EndTime,
    TimeSpan? Duration);
