﻿namespace VpnHood.Core.Toolkit.Jobs;

public class JobOptions
{
    public static TimeSpan DefaultPeriod { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan Period { get; init; } = DefaultPeriod;
    public string? Name { get; init; }
    public TimeSpan? DueTime { get; init; }
    public int? MaxRetry { get; init; }
    public bool AutoStart { get; init; } = true;
}