namespace LiveCaptionsTranslator.lifecycle
{
    internal enum ApplicationShutdownPhase
    {
        CancelBackgroundLoops,
        ObserveBackgroundLoop,
        StopCaptionSource,
        DisposeCaptionSource
    }

    internal sealed record ApplicationShutdownFailure(
        ApplicationShutdownPhase Phase,
        Exception Exception);

    internal static class ApplicationShutdownCoordinator
    {
        internal static async Task<IReadOnlyList<ApplicationShutdownFailure>> RunAsync(
            Action cancelBackgroundLoops,
            IReadOnlyList<Task> backgroundLoops,
            Func<Task> stopCaptionSource,
            Func<ValueTask> disposeCaptionSource)
        {
            ArgumentNullException.ThrowIfNull(cancelBackgroundLoops);
            ArgumentNullException.ThrowIfNull(backgroundLoops);
            ArgumentNullException.ThrowIfNull(stopCaptionSource);
            ArgumentNullException.ThrowIfNull(disposeCaptionSource);

            var failures = new List<ApplicationShutdownFailure>();

            TryRun(
                cancelBackgroundLoops,
                ApplicationShutdownPhase.CancelBackgroundLoops,
                failures);

            foreach (var loop in backgroundLoops)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (loop.IsCanceled)
                {
                }
                catch (Exception exception)
                {
                    AddTaskFailures(
                        loop,
                        exception,
                        ApplicationShutdownPhase.ObserveBackgroundLoop,
                        failures);
                }
            }

            await TryRunAsync(
                stopCaptionSource,
                ApplicationShutdownPhase.StopCaptionSource,
                failures).ConfigureAwait(false);
            await TryRunAsync(
                async () => await disposeCaptionSource().ConfigureAwait(false),
                ApplicationShutdownPhase.DisposeCaptionSource,
                failures).ConfigureAwait(false);

            return failures;
        }

        private static void TryRun(
            Action operation,
            ApplicationShutdownPhase phase,
            ICollection<ApplicationShutdownFailure> failures)
        {
            try
            {
                operation();
            }
            catch (Exception exception)
            {
                failures.Add(new ApplicationShutdownFailure(phase, exception));
            }
        }

        private static async Task TryRunAsync(
            Func<Task> operation,
            ApplicationShutdownPhase phase,
            ICollection<ApplicationShutdownFailure> failures)
        {
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(new ApplicationShutdownFailure(phase, exception));
            }
        }

        private static void AddTaskFailures(
            Task task,
            Exception observedException,
            ApplicationShutdownPhase phase,
            ICollection<ApplicationShutdownFailure> failures)
        {
            var taskExceptions = task.Exception?.Flatten().InnerExceptions;
            if (taskExceptions == null || taskExceptions.Count == 0)
            {
                failures.Add(new ApplicationShutdownFailure(phase, observedException));
                return;
            }

            foreach (var exception in taskExceptions)
                failures.Add(new ApplicationShutdownFailure(phase, exception));
        }
    }
}
