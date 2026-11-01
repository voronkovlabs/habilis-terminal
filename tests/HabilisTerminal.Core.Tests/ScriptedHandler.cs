using HabilisTerminal.Core.Operations;

namespace HabilisTerminal.Core.Tests;

/// <summary>Answers operation questions from delegates and records what was asked.</summary>
internal sealed class ScriptedHandler : IOperationHandler
{
    public Func<ConflictInfo, ConflictDecision> OnConflict { get; init; } = _ => throw new InvalidOperationException("Unexpected conflict.");

    public Func<OperationError, ErrorAction> OnError { get; init; } = e => throw new InvalidOperationException($"Unexpected error: {e.Message}", e.Exception);

    public Func<Confirmation, ConfirmationAnswer> OnConfirm { get; init; } = c => throw new InvalidOperationException($"Unexpected confirmation: {c.Kind}");

    public Action<OperationProgress>? OnProgress { get; init; }

    public List<ConflictInfo> Conflicts { get; } = [];

    public List<OperationError> Errors { get; } = [];

    public List<Confirmation> Confirmations { get; } = [];

    public OperationProgress? LastProgress { get; private set; }

    public ConflictDecision ResolveConflict(ConflictInfo conflict)
    {
        Conflicts.Add(conflict);
        return OnConflict(conflict);
    }

    public ErrorAction ResolveError(OperationError failure)
    {
        Errors.Add(failure);
        return OnError(failure);
    }

    public ConfirmationAnswer Confirm(Confirmation confirmation)
    {
        Confirmations.Add(confirmation);
        return OnConfirm(confirmation);
    }

    public void ReportProgress(OperationProgress progress)
    {
        LastProgress = progress;
        OnProgress?.Invoke(progress);
    }
}
