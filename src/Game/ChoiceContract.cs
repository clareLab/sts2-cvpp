using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;

namespace cvpp;

internal sealed record ChoiceContract(string Kind, bool Single = false, bool CanSkip = false)
{
    internal static readonly AsyncLocal<ChoiceContract?> Current = new();
}

[HarmonyPatch]
internal static class ChoiceContractPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => typeof(CardSelectCmd).GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => method.Name is nameof(CardSelectCmd.FromChooseACardScreen) or nameof(CardSelectCmd.FromSimpleGrid)
            or nameof(CardSelectCmd.FromSimpleGridForRewards) or nameof(CardSelectCmd.FromCombatPile)
            or nameof(CardSelectCmd.FromHand) or nameof(CardSelectCmd.FromHandForUpgrade)
            or nameof(CardSelectCmd.FromDeckGeneric) or nameof(CardSelectCmd.FromDeckForUpgrade)
            or nameof(CardSelectCmd.FromDeckForTransformation) or nameof(CardSelectCmd.FromDeckForEnchantment));

    private static void Prefix(MethodBase __originalMethod, object[] __args, out ChoiceContract? __state)
    {
        __state = ChoiceContract.Current.Value;
        string name = __originalMethod.Name;
        ChoiceContract.Current.Value = name switch
        {
            nameof(CardSelectCmd.FromChooseACardScreen) => new("index", true, (bool)__args[3]),
            nameof(CardSelectCmd.FromSimpleGrid) or nameof(CardSelectCmd.FromSimpleGridForRewards) => new("index"),
            nameof(CardSelectCmd.FromCombatPile) or nameof(CardSelectCmd.FromHand) or nameof(CardSelectCmd.FromHandForUpgrade) => new("combat"),
            _ => new("deck")
        };
    }

    private static void Postfix(ChoiceContract? __state) => ChoiceContract.Current.Value = __state;
}
