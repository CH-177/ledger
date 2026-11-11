using System.Collections.Concurrent;
using System.Text.Json;
using Ledger.Core;
using Ledger.Core.Assistant;
using Microsoft.Extensions.Logging;

namespace Ledger.App.Services;

/// <summary>
/// In-memory queue of Grokbot write proposals. Parsing never mutates data; Approve runs
/// <see cref="MutationTools.ApplyAsync"/> once the user confirms in the UI.
/// </summary>
public sealed class ProposalService(MutationTools mutations, TimeProvider time, ILogger<ProposalService> logger)
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, MutationProposal> _items = new();

    public event EventHandler? Changed;

    public IReadOnlyList<MutationProposal> Pending
    {
        get
        {
            ExpireDue();
            return _items.Values.Where(p => p.Status == ProposalStatus.Pending)
                .OrderBy(p => p.CreatedAt).ToList();
        }
    }

    public MutationProposal? Current => Pending.FirstOrDefault();

    public int PendingCount => Pending.Count;

    public async Task<MutationProposal> ProposeAsync(string toolName, JsonElement input, CancellationToken ct = default)
    {
        ExpireDue();
        var (kind, summary, payload) = await mutations.ParseAsync(toolName, input, ct);
        var now = time.GetUtcNow();
        var proposal = new MutationProposal
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = kind,
            ToolName = toolName,
            Summary = summary,
            PayloadJson = payload,
            CreatedAt = now,
            ExpiresAt = now + DefaultTtl,
        };
        _items[proposal.Id] = proposal;
        Changed?.Invoke(this, EventArgs.Empty);
        return proposal;
    }

    public MutationProposal? Get(string id)
    {
        ExpireDue();
        return _items.TryGetValue(id, out var p) ? p : null;
    }

    public async Task<MutationProposal> ApproveAsync(string id, CancellationToken ct = default)
    {
        ExpireDue();
        if (!_items.TryGetValue(id, out var proposal))
        {
            throw new LedgerValidationException("That proposal no longer exists.");
        }
        if (proposal.Status != ProposalStatus.Pending)
        {
            throw new LedgerValidationException($"That proposal is already {proposal.Status.ToString().ToLowerInvariant()}.");
        }

        try
        {
            proposal.ResultJson = await mutations.ApplyAsync(proposal.Kind, proposal.PayloadJson, ct);
            proposal.Status = ProposalStatus.Approved;
        }
        catch (Exception ex) when (ex is LedgerValidationException or not OperationCanceledException)
        {
            proposal.Status = ProposalStatus.Failed;
            proposal.Error = ex.Message;
            logger.LogWarning(ex, "Grokbot proposal {Id} failed on approve", id);
            Changed?.Invoke(this, EventArgs.Empty);
            throw;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return proposal;
    }

    public MutationProposal Deny(string id)
    {
        ExpireDue();
        if (!_items.TryGetValue(id, out var proposal))
        {
            throw new LedgerValidationException("That proposal no longer exists.");
        }
        if (proposal.Status != ProposalStatus.Pending)
        {
            throw new LedgerValidationException($"That proposal is already {proposal.Status.ToString().ToLowerInvariant()}.");
        }
        proposal.Status = ProposalStatus.Denied;
        Changed?.Invoke(this, EventArgs.Empty);
        return proposal;
    }

    private void ExpireDue()
    {
        var now = time.GetUtcNow();
        var changed = false;
        foreach (var p in _items.Values.Where(p => p.Status == ProposalStatus.Pending && p.ExpiresAt <= now))
        {
            p.Status = ProposalStatus.Expired;
            changed = true;
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
