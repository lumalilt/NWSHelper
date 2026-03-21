using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;

namespace NWSHelper.Cli.Services;

/// <summary>
/// Renders progress updates for long-running CLI tasks.
/// </summary>
public interface IProgressRenderer
{
    /// <summary>
    /// Starts a new task.
    /// </summary>
    /// <param name="taskKey">Unique key for the task.</param>
    /// <param name="description">Task description.</param>
    /// <param name="maxValue">Optional maximum value for percentage completion.</param>
    /// <param name="isIndeterminate">Whether the task is indeterminate.</param>
    void StartTask(string taskKey, string description, double? maxValue = null, bool isIndeterminate = false);

    /// <summary>
    /// Updates a task's progress.
    /// </summary>
    /// <param name="taskKey">Unique key for the task.</param>
    /// <param name="value">Current progress value.</param>
    /// <param name="maxValue">Optional maximum value for percentage completion.</param>
    /// <param name="description">Optional task description override.</param>
    void UpdateTask(string taskKey, double? value = null, double? maxValue = null, string? description = null);

    /// <summary>
    /// Marks a task as completed.
    /// </summary>
    /// <param name="taskKey">Unique key for the task.</param>
    /// <param name="description">Optional completion description.</param>
    void CompleteTask(string taskKey, string? description = null);

    /// <summary>
    /// Marks a task as failed.
    /// </summary>
    /// <param name="taskKey">Unique key for the task.</param>
    /// <param name="description">Optional failure description.</param>
    void SetError(string taskKey, string? description = null);

    /// <summary>
    /// Pauses progress rendering until the returned scope is disposed.
    /// </summary>
    /// <returns>A disposable scope that resumes rendering when disposed.</returns>
    IDisposable? Pause();
}

/// <summary>
/// Spectre.Console-based progress renderer.
/// </summary>
public sealed class SpectreProgressRenderer : IProgressRenderer
{
    private readonly Dictionary<string, ProgressTask> tasks = new(StringComparer.OrdinalIgnoreCase);
    private ProgressContext? context;
    private int pauseDepth;

    /// <summary>
    /// Runs a progress rendering scope and executes an action inside it.
    /// </summary>
    /// <param name="action">Action to execute within the progress rendering scope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RunAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        var progress = AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .AutoRefresh(false)
            .Columns(new ProgressColumn[]
            {
                new SpinnerColumn(),
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new ElapsedTimeColumn()
            });

        await progress.StartAsync(async ctx =>
        {
            context = ctx;
            Interlocked.Exchange(ref pauseDepth, 0);
            await action();
        });
    }

    /// <inheritdoc />
    public void StartTask(string taskKey, string description, double? maxValue = null, bool isIndeterminate = false)
    {
        EnsureContext();
        if (tasks.ContainsKey(taskKey))
        {
            return;
        }

        var task = context!.AddTask(description);
        task.IsIndeterminate = isIndeterminate;
        if (maxValue.HasValue)
        {
            task.MaxValue = maxValue.Value;
        }

        tasks[taskKey] = task;
        RefreshIfActive();
    }

    /// <inheritdoc />
    public void UpdateTask(string taskKey, double? value = null, double? maxValue = null, string? description = null)
    {
        if (!tasks.TryGetValue(taskKey, out var task))
        {
            return;
        }

        if (description is not null)
        {
            task.Description = description;
        }

        if (maxValue.HasValue)
        {
            task.MaxValue = maxValue.Value;
            if (maxValue.Value > 0)
            {
                task.IsIndeterminate = false;
            }
        }

        if (value.HasValue)
        {
            task.Value = value.Value;
        }

        RefreshIfActive();
    }

    /// <inheritdoc />
    public void CompleteTask(string taskKey, string? description = null)
    {
        if (!tasks.TryGetValue(taskKey, out var task))
        {
            return;
        }

        if (task.MaxValue <= 0)
        {
            task.MaxValue = 1;
        }

        task.Value = task.MaxValue;
        task.StopTask();
        task.Description = BuildCompletionDescription(task.Description, description, isError: false);
        RefreshIfActive();
    }

    /// <inheritdoc />
    public void SetError(string taskKey, string? description = null)
    {
        if (!tasks.TryGetValue(taskKey, out var task))
        {
            return;
        }

        task.StopTask();
        task.Description = BuildCompletionDescription(task.Description, description, isError: true);
        RefreshIfActive();
    }

    /// <inheritdoc />
    public IDisposable? Pause()
    {
        if (context is null)
        {
            return null;
        }

        Interlocked.Increment(ref pauseDepth);
        return new PauseScope(this);
    }

    private void EnsureContext()
    {
        if (context is null)
        {
            throw new InvalidOperationException("Progress renderer has not been started.");
        }
    }

    private void RefreshIfActive()
    {
        if (context is null || Volatile.Read(ref pauseDepth) > 0)
        {
            return;
        }

        context.Refresh();
    }

    private void Resume()
    {
        if (context is null)
        {
            return;
        }

        var remaining = Interlocked.Decrement(ref pauseDepth);
        if (remaining <= 0)
        {
            Interlocked.Exchange(ref pauseDepth, 0);
            RefreshIfActive();
        }
    }

    private static string BuildCompletionDescription(string existing, string? overrideText, bool isError)
    {
        var label = overrideText ?? existing;
        var icon = isError ? "[red]✗[/]" : "[green]✓[/]";
        return $"{icon} {label}";
    }

    private sealed class PauseScope : IDisposable
    {
        private SpectreProgressRenderer? owner;

        public PauseScope(SpectreProgressRenderer owner)
        {
            this.owner = owner;
        }

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            current?.Resume();
        }
    }
}

