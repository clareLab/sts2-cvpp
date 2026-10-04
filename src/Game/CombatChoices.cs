using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace cvpp;

internal sealed class CombatChoices : ICardSelector
{
    private TaskCompletionSource<IEnumerable<CardModel>>? _completion;
    internal SelectionSet<CardModel>? Pending { get; private set; }

    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        if (Pending != null) throw new NotSupportedException("Concurrent card selections are unsupported.");
        Pending = new SelectionSet<CardModel>(options, minSelect, maxSelect);
        _completion = new TaskCompletionSource<IEnumerable<CardModel>>();
        return _completion.Task;
    }

    internal void Complete(uint index) => Complete(Pending?[index]
        ?? throw new InvalidOperationException("No card selection is pending."));

    internal void FinishAbandoned() => Complete(Pending?.Completion
        ?? throw new InvalidOperationException("No card selection is pending."));

    private void Complete(CardModel[] cards)
    {
        var source = _completion ?? throw new InvalidOperationException("No card selection is pending.");
        Pending = null;
        _completion = null;
        source.SetResult(cards);
    }

    public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives) => throw new NotSupportedException("Combat search does not choose card rewards.");
}
