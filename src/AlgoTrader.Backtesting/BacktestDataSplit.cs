namespace AlgoTrader.Backtesting;

/// <summary>Named data partition used to prevent development code from inspecting out-of-sample data.</summary>
public enum BacktestDataPartition
{
    Training,
    Validation,
    OutOfSample
}

/// <summary>Half-open boundaries for a one-way training, validation, and out-of-sample split.</summary>
public sealed record BacktestDataSplit(DateTimeOffset TrainingEndUtc, DateTimeOffset ValidationEndUtc)
{
    /// <summary>Ensures ordered split boundaries before a run starts.</summary>
    public void Validate(DateTimeOffset dataStartUtc, DateTimeOffset dataEndUtc)
    {
        if (dataStartUtc >= dataEndUtc)
            throw new ArgumentException("Data range must be non-empty and ordered.", nameof(dataEndUtc));
        if (TrainingEndUtc <= dataStartUtc || ValidationEndUtc <= TrainingEndUtc || ValidationEndUtc >= dataEndUtc)
            throw new ArgumentException("Split boundaries must be strictly ordered within the data range.");
    }

    /// <summary>Returns the partition for a timestamp after validation has occurred.</summary>
    public BacktestDataPartition GetPartition(DateTimeOffset timestampUtc) => timestampUtc < TrainingEndUtc
        ? BacktestDataPartition.Training
        : timestampUtc < ValidationEndUtc
            ? BacktestDataPartition.Validation
            : BacktestDataPartition.OutOfSample;
}
