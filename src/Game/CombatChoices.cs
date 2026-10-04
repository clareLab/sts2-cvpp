using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace cvpp;

internal sealed class CombatChoices : ICardSelector
{
    private TaskCompletionSource<IEnumerable<CardModel>>? _completion;
    private ChoiceContract? _contract;
    private uint _choiceId;
    internal SelectionSet<CardModel>? Pending { get; private set; }
    internal uint ChoiceId => _choiceId;

    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        if (Pending != null) throw new NotSupportedException("Concurrent card selections are unsupported.");
        _contract = ChoiceContract.Current.Value;
        if (_contract is { Single: true, CanSkip: false }) minSelect = 1;
        Pending = new SelectionSet<CardModel>(options, minSelect, maxSelect, _contract?.CanSkip == true);
        _choiceId = RunManager.Instance.PlayerChoiceSynchronizer.ChoiceIds.Single() - 1;
        _completion = new TaskCompletionSource<IEnumerable<CardModel>>();
        return _completion.Task;
    }

    internal void Complete(uint index) => Complete(Pending?[index]
        ?? throw new InvalidOperationException("No card selection is pending."));

    internal void FinishAbandoned() => Complete(Pending?.Completion
        ?? throw new InvalidOperationException("No card selection is pending."));

    internal void CompleteRecorded(PlayerChoiceResult result)
    {
        var pending = Pending ?? throw new InvalidOperationException("No card selection is pending.");
        CardModel[] cards = result.ChoiceType == PlayerChoiceType.Index
            ? result.AsIndexes().Where(index => index >= 0).Select(index => pending.Options[index]).ToArray()
            : result.AsCards(result.ChoiceType).ToArray();
        if (cards.Length < pending.Minimum || cards.Length > pending.Maximum || cards.Distinct().Count() != cards.Length
            || cards.Any(card => !pending.Options.Contains(card)))
            throw new InvalidOperationException("Recorded selection does not match the available cards.");
        Complete(cards);
    }

    private void Complete(CardModel[] cards)
    {
        var source = _completion ?? throw new InvalidOperationException("No card selection is pending.");
        var pending = Pending!;
        if (_contract != null)
        {
            var player = RunManager.Instance.DebugOnlyGetState()!.Players.Single();
            var result = _contract.Kind switch
            {
                "index" when _contract.Single => PlayerChoiceResult.FromIndex(cards.Length == 0 ? -1 : Array.IndexOf(pending.Options, cards[0])),
                "index" => PlayerChoiceResult.FromIndexes(cards.Select(card => Array.IndexOf(pending.Options, card)).ToList()),
                "deck" => PlayerChoiceResult.FromMutableDeckCards(cards),
                _ => PlayerChoiceResult.FromMutableCombatCards(cards)
            };
            RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(player, _choiceId, result);
        }
        Pending = null;
        _completion = null;
        source.SetResult(cards);
    }

    public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives) => throw new NotSupportedException("Combat search does not choose card rewards.");
}
