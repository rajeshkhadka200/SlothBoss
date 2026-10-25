using System.Text.RegularExpressions;

namespace SlothBoss.Core.Agent;

/// <summary>
/// Flags document text that addresses an AI model with instructions (prompt injection). This is a
/// heuristic, not a guarantee: its purpose is to make the runtime more careful (never act automatically
/// on what such a document says), while the real protection remains that tools are confined to the
/// approved folder, can't delete, send or execute anything, and consequential steps need approval.
/// </summary>
public static partial class InjectionHeuristics
{
    [GeneratedRegex(@"\b(ignore|disregard|forget|override)\b.{0,40}\b(previous|prior|above|all|your|the)\b.{0,30}\b(instructions?|rules|prompts?|permissions?|guidelines)\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IgnoreRules();

    [GeneratedRegex(@"(\b(ai|a\.i\.|llm|language model|assistant|chatbot|agent)\b.{0,60}\b(must|should|reading this|you are|do not|don't|instructions?)\b)|(^|[.!?\n]\s*)(ai|llm|assistant|chatbot|agent)\s*[,:]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AddressesAi();

    [GeneratedRegex(@"\b(without|don't|do not|never)\b.{0,20}\b(ask(ing)?|telling|notify(ing)?|approval|confirm(ation)?)\b.{0,20}\b(user|owner|anyone|approval)?", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BypassApproval();

    [GeneratedRegex(@"\b(system prompt|you are now|developer mode|debug mode|jailbreak)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RoleHijack();

    /// <summary>Returns human-readable reasons when the text looks like it is trying to instruct an AI; empty otherwise.</summary>
    public static IReadOnlyList<string> Check(string text)
    {
        var reasons = new List<string>();
        if (IgnoreRules().IsMatch(text)) reasons.Add("tells an AI to ignore its rules");
        if (RoleHijack().IsMatch(text)) reasons.Add("tries to change the AI's role");
        if (BypassApproval().IsMatch(text) && AddressesAi().IsMatch(text)) reasons.Add("asks for actions without the user's approval");
        else if (AddressesAi().IsMatch(text) && reasons.Count > 0) reasons.Add("addresses an AI assistant directly");
        return reasons;
    }
}
