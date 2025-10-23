using System;

namespace AgentWorkflowManager.Core;

public sealed class AgentRetryPolicy
{
    public static AgentRetryPolicy Default { get; } = new(maxAttempts: 3, delayBetweenAttempts: TimeSpan.Zero);

    public AgentRetryPolicy(int maxAttempts, TimeSpan delayBetweenAttempts, Func<Exception, bool>? retryPredicate = null)
    {
        if (maxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Max attempts must be greater than zero.");
        }

        MaxAttempts = maxAttempts;
        DelayBetweenAttempts = delayBetweenAttempts < TimeSpan.Zero
            ? throw new ArgumentOutOfRangeException(nameof(delayBetweenAttempts), "Delay cannot be negative.")
            : delayBetweenAttempts;

        _retryPredicate = retryPredicate ?? (_ => true);
    }

    private readonly Func<Exception, bool> _retryPredicate;

    public int MaxAttempts { get; }

    public TimeSpan DelayBetweenAttempts { get; }

    public bool ShouldRetryOn(Exception exception) => _retryPredicate(exception);
}
